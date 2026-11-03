using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Transport;

namespace Netrun.Core.Entities;

/// <summary>
/// Единоличный владелец процессов: целостность, residence, статусы, persona и lifecycle живут здесь.
/// Native стороны получают неизменяемые проекции и выполняют только явно выданные действия.
/// </summary>
public sealed class EntityWorld : IDisposable
{
    /// <summary>Mutable actor скрыт внутри owner; наружу выдаётся только immutable View.</summary>
    private sealed class Actor(EntityDefinition definition)
    {
        internal EntityView View = new(definition.Actor, 1, 1, definition.Kind, definition.Node,
            definition.MaxIntegrity, definition.MaxIntegrity, definition.Persona, definition.FactionId,
            definition.PrincipalId, definition.ProfileId, definition.Autonomous, 0, 0, 1, 0, true, null,
            definition.ExpiresAtDs);
        internal EntityNativeEffect? Pending;
        internal bool PendingHandedOff;
        internal Deadline? Deadline;
    }

    /// <summary>Завершённые операции сохраняют только fingerprint и скалярный outcome.</summary>
    private sealed record History(byte[] Fingerprint, TransportAdmission Admission);
    /// <summary>Deadline хранит поколение и точную ревизию, отменённые таймеры не воздействуют на новые процессы.</summary>
    private sealed record Deadline(ActorKey Actor, ulong Revision, int At);

    private readonly string _binding;
    private readonly EntityWorldOptions _options;
    private readonly Dictionary<string, Actor> _actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _generations = new(StringComparer.Ordinal);
    private readonly Queue<TransportEvent> _events = [];
    private readonly Dictionary<RequestKey, History> _history = [];
    private readonly Queue<RequestKey> _historyOrder = [];
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    private readonly ProducerAdmissionFence _fence;
    private readonly SortedSet<Deadline> _deadlines = new(Comparer<Deadline>.Create((left, right) =>
    {
        var result = left.At.CompareTo(right.At);
        if (result == 0) result = StringComparer.Ordinal.Compare(left.Actor.Id, right.Actor.Id);
        if (result == 0) result = left.Actor.Generation.CompareTo(right.Actor.Generation);
        return result == 0 ? left.Revision.CompareTo(right.Revision) : result;
    }));
    private readonly Func<EntityView, NodeKey, string?>? _movementDenial;
    private int _now;
    private bool _busy;
    private bool _disposed;

    /// <summary>Число живых и ожидающих native teardown процессов.</summary>
    public int Count => _actors.Count;
    /// <summary>Число непрочитанных immutable projections/effects.</summary>
    public int EventCount => _events.Count;
    /// <summary>Монотонная версия registry/read-model; cooperative readers отвергают смешанный снимок.</summary>
    public ulong StateVersion { get; private set; }
    /// <summary>Не больше одного deadline на живой процесс, независимо от частоты обновления статусов.</summary>
    public int PendingDeadlineCount => _deadlines.Count;

    /// <summary>Создаёт пустой registry одного binding без native ссылок, таймеров и I/O.</summary>
    public EntityWorld(string bindingEpoch, EntityWorldOptions? options = null,
        Func<EntityView, NodeKey, string?>? movementDenial = null)
    {
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        _binding = bindingEpoch;
        _movementDenial = movementDenial;
        _options = options ?? new();
        if (_options.MaxEntities < 1 || _options.MaxTrackedIds < 1 || _options.MaxEvents < 1 ||
            _options.MaxProducers < 1 || _options.HistoryCapacity is < 1 or > 4096)
            throw new ArgumentException("Invalid entity options.", nameof(options));
        _fence = new(_watermarks, _options.MaxProducers);
    }

    /// <summary>Читает точное поколение; recycled identity не разрешается в новый actor.</summary>
    public EntityView? Read(ActorKey key) => _actors.TryGetValue(key.Id, out var actor) && actor.View.Actor == key ? actor.View : null;

    /// <summary>Перечисляет immutable views без копирования registry; caller проверяет StateVersion между шагами.</summary>
    public IEnumerable<EntityView> Enumerate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var actor in _actors.Values) yield return actor.View;
    }

    /// <summary>Проверяет replay и резервирует output до любой мутации процесса.</summary>
    public TransportAdmission Submit(EntityOperation operation, int simulationTimeDs)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Enter();
        try
        {
            AdvanceClock(simulationTimeDs);
            var fingerprint = Fingerprint(operation);
            if (fingerprint is null) return new(AdmissionStatus.Rejected, "invalid_entity_operation", operation.Key);
            if (_history.TryGetValue(operation.Key, out var previous))
                return CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint)
                    ? previous.Admission with { Status = AdmissionStatus.Duplicate }
                    : new(AdmissionStatus.Rejected, "request_conflict", operation.Key);
            if (_watermarks.TryGetValue(operation.Key.ProducerId, out var watermark) && operation.Key.Sequence <= watermark)
                return new(AdmissionStatus.Rejected, "request_expired", operation.Key);
            if (_fence.Blocks(operation.Key)) return _fence.Complete(new(AdmissionStatus.WouldBlock, "producer_backpressure", operation.Key));
            if (!_watermarks.ContainsKey(operation.Key.ProducerId) && _watermarks.Count >= _options.MaxProducers)
                return _fence.Complete(new(AdmissionStatus.WouldBlock, "producer_capacity", operation.Key));
            if (_events.Count >= _options.MaxEvents) return _fence.Complete(new(AdmissionStatus.WouldBlock, "entity_output_pressure", operation.Key));
            var code = Apply(operation);
            var admission = new TransportAdmission(code == "accepted" ? AdmissionStatus.Accepted : AdmissionStatus.Rejected,
                code, operation.Key, code == "accepted" ? RequestState.Queued : RequestState.Failed);
            // Domain capacity — terminal отказ: FIFO head не может ждать Delete, стоящий за ним.
            _watermarks[operation.Key.ProducerId] = operation.Key.Sequence;
            _history.Add(operation.Key, new(fingerprint, admission));
            _historyOrder.Enqueue(operation.Key);
            if (_historyOrder.Count > _options.HistoryCapacity) _history.Remove(_historyOrder.Dequeue());
            _fence.Complete(admission);
            return admission;
        }
        finally { _busy = false; }
    }

    /// <summary>Применяет подтверждение один раз. Повтор после handoff не создаёт второй переход.</summary>
    public TransportActionResult Complete(EntityEffectResult result, int simulationTimeDs)
    {
        Enter();
        try
        {
            AdvanceClock(simulationTimeDs);
            if (!IsValidResult(result)) return new(false, "invalid_entity_result");
            if (result.BindingEpoch != _binding) return new(false, "epoch_mismatch");
            if (!_actors.TryGetValue(result.Actor.Id, out var actor) || actor.View.Actor != result.Actor ||
                actor.Pending is not { } effect || effect.EffectId != result.EffectId ||
                effect.ResidenceRevision != result.ResidenceRevision || !actor.PendingHandedOff) return new(false, "entity_effect_unknown");
            if (_events.Count >= _options.MaxEvents) return new(false, "entity_output_pressure", true);
            actor.Pending = null;
            actor.View = actor.View with
            {
                PendingEffectId = null,
                Revision = Next(actor.View.Revision),
                ResidenceRevision = result.Success ? Next(actor.View.ResidenceRevision) : actor.View.ResidenceRevision,
                Node = result.Success ? effect.Destination : actor.View.Node,
                NextHopDs = result.Success ? DeadlineAfter(10) : actor.View.NextHopDs
            };
            Changed(actor, effect.Key, result.Success ? "moved" : "native_rejected");
            return new(true, result.Success ? "moved" : "native_rejected");
        }
        finally { _busy = false; }
    }

    /// <summary>Продвигает только due status/lifetime записи; бюджет не зависит от общего числа процессов.</summary>
    public int Advance(int simulationTimeDs, int workBudget)
    {
        Enter();
        try
        {
            AdvanceClock(simulationTimeDs);
            ArgumentOutOfRangeException.ThrowIfNegative(workBudget);
            var work = 0;
            while (work < workBudget && _events.Count < _options.MaxEvents &&
                _deadlines.Min is { } due && due.At <= _now)
            {
                _deadlines.Remove(due);
                work++;
                if (!_actors.TryGetValue(due.Actor.Id, out var actor) || actor.View.Actor != due.Actor ||
                    actor.View.Revision != due.Revision) continue;
                actor.Deadline = null;
                if (actor.View.Kind != EntityKind.Bot && actor.View.ExpiresAtDs is { } expiry && expiry <= _now)
                    actor.View = actor.View with { Alive = false, Autonomous = false, PendingEffectId = null, Revision = Next(actor.View.Revision) };
                else actor.View = actor.View with
                {
                    Revision = Next(actor.View.Revision),
                    StunnedUntilDs = actor.View.StunnedUntilDs <= _now ? 0 : actor.View.StunnedUntilDs,
                    ShieldUntilDs = actor.View.ShieldUntilDs <= _now ? 0 : actor.View.ShieldUntilDs,
                    ShieldFactor = actor.View.ShieldUntilDs <= _now ? 1 : actor.View.ShieldFactor
                };
                if (!actor.View.Alive) actor.Pending = null;
                Changed(actor, new("entity.lifecycle", actor.View.Revision), actor.View.Alive ? "status_expired" : "expired");
            }
            return work;
        }
        finally { _busy = false; }
    }

    /// <summary>Выдаёт только существующий native effect; удалённые/отменённые действия отзываются до handoff.</summary>
    public bool TryDequeueEvent(out TransportEvent? result)
    {
        Enter();
        try
        {
            while (_events.TryDequeue(out var next))
            {
                if (next is EntityNativeEffect effect)
                {
                    if (!_actors.TryGetValue(effect.Actor.Id, out var actor) || actor.Pending != effect) continue;
                    actor.PendingHandedOff = true;
                }
                result = next;
                return true;
            }
            result = null;
            return false;
        }
        finally { _busy = false; }
    }

    /// <summary>Закрывает старый binding без replay незавершённых native действий.</summary>
    public void Dispose()
    {
        if (_busy) throw new InvalidOperationException("Entity world is already executing.");
        _disposed = true;
        _actors.Clear(); _generations.Clear(); _events.Clear(); _history.Clear(); _historyOrder.Clear();
        _watermarks.Clear(); _fence.Clear(); _deadlines.Clear();
    }

    /// <summary>Применяет закрытый intent; topology/ACL admission движения принадлежит composition owner.</summary>
    private string Apply(EntityOperation operation)
    {
        if (operation.Action is CreateEntity create)
        {
            if (_actors.ContainsKey(operation.Actor.Id)) return "entity_exists";
            if (_generations.TryGetValue(operation.Actor.Id, out var generation) && operation.Actor.Generation <= generation) return "stale_actor";
            if (_actors.Count >= _options.MaxEntities) return "entity_capacity";
            if (!_generations.ContainsKey(operation.Actor.Id) && _generations.Count >= _options.MaxTrackedIds) return "entity_id_capacity";
            var created = new Actor(create.Definition);
            if (created.View.Kind != EntityKind.Bot && created.View.ExpiresAtDs is { } expires && expires <= _now)
                created.View = created.View with { Alive = false, Autonomous = false };
            _actors.Add(operation.Actor.Id, created);
            _generations[operation.Actor.Id] = operation.Actor.Generation;
            Changed(created, operation.Key, created.View.Alive ? "created" : "expired");
            return "accepted";
        }
        if (!_actors.TryGetValue(operation.Actor.Id, out var actor) || actor.View.Actor != operation.Actor) return "entity_unknown";
        var view = actor.View;
        if (operation.ExpectedRevision is { } expected && expected != view.Revision) return "stale_actor_revision";
        if (operation.Action is DeleteEntity deletion)
        {
            if (actor.Deadline is { } deadline) _deadlines.Remove(deadline);
            _actors.Remove(operation.Actor.Id);
            StateVersion = Next(StateVersion);
            _events.Enqueue(new EntityChanged(_binding, operation.Key, operation.Actor, null, deletion.Code));
            return "accepted";
        }
        if (view.Alive && view.Kind != EntityKind.Bot && view.ExpiresAtDs is { } expiry && expiry <= _now)
        {
            actor.Pending = null;
            actor.View = view with { Alive = false, Autonomous = false, PendingEffectId = null, Revision = Next(view.Revision) };
            Changed(actor, operation.Key, "expired");
            return operation.Action is ReadEntity ? "accepted" : "entity_expired";
        }
        if (operation.Action is ReadEntity) { _events.Enqueue(new EntityChanged(_binding, operation.Key, operation.Actor, view, "snapshot")); return "accepted"; }
        if (!view.Alive) return "entity_dead";
        switch (operation.Action)
        {
            case DamageEntity damage:
                var amount = damage.Amount * (!damage.IgnoreShield && _now < view.ShieldUntilDs ? view.ShieldFactor : 1);
                actor.View = view with { Integrity = view.Integrity - amount, Alive = view.Integrity - amount > 0 };
                break;
            case HealEntity heal:
                actor.View = view with { Integrity = Math.Min(view.MaxIntegrity, view.Integrity + heal.Amount) };
                break;
            case StunEntity stun:
                actor.View = view with { StunnedUntilDs = Math.Max(view.StunnedUntilDs, DeadlineAfter(stun.DurationDs)) };
                break;
            case ShieldEntity shield:
                var factor = Math.Clamp(1 - shield.Percent / 100, 0.1, 1);
                if (_now < view.ShieldUntilDs && factor > view.ShieldFactor) return "weaker_shield";
                actor.View = view with { ShieldUntilDs = DeadlineAfter(shield.DurationDs), ShieldFactor = factor };
                break;
            case ControlEntity control: actor.View = view with { Autonomous = control.Autonomous }; break;
            case SetEntityPersona persona: actor.View = view with { Persona = persona.Persona }; break;
            case SetEntityFaction faction: actor.View = view with { FactionId = faction.FactionId, PrincipalId = faction.PrincipalId }; break;
            case SetEntityCombatState status:
                actor.View = view with
                {
                    ShieldFactor = status.ShieldFactor,
                    ShieldUntilDs = (int)Math.Min(int.MaxValue, status.ShieldUntilDs),
                    StunnedUntilDs = (int)Math.Min(int.MaxValue, status.StunUntilDs),
                    ShieldedByTurn = status.ShieldUntilDs == long.MaxValue,
                    StunnedByTurn = status.StunUntilDs == long.MaxValue
                };
                break;
            case AdjustEntityTrace trace: actor.View = view with { Trace = Math.Clamp(view.Trace + trace.Delta, 0, 100) }; break;
            case ClearEntityCombatGrace: actor.View = view with { CombatIgnoreUntilDs = 0 }; break;
            case MoveEntity move:
                if (actor.Pending is not null) return "entity_effect_pending";
                if (_now < view.NextHopDs || _now < view.StunnedUntilDs) return "entity_move_cooldown";
                if (view.Node == move.Destination) return "same_node";
                var denied = _movementDenial?.Invoke(view, move.Destination) ?? (_movementDenial is null ? "authority_unavailable" : null);
                if (denied is not null) return denied;
                var effect = new EntityNativeEffect(_binding, operation.Key, operation.Actor,
                    _binding + "/entity/" + operation.Key.Id, view.ResidenceRevision, view.Node, move.Destination);
                actor.Pending = effect;
                actor.PendingHandedOff = false;
                actor.View = view with { PendingEffectId = effect.EffectId, Revision = Next(view.Revision) };
                StateVersion = Next(StateVersion);
                _events.Enqueue(effect);
                Schedule(actor);
                return "accepted";
            default: return "entity_action_unknown";
        }
        actor.View = actor.View with { Revision = Next(view.Revision) };
        if (!actor.View.Alive)
        {
            actor.Pending = null;
            actor.View = actor.View with { Autonomous = false, PendingEffectId = null };
        }
        Changed(actor, operation.Key, actor.View.Alive ? "changed" : "dissolved");
        return "accepted";
    }

    /// <summary>Добавляет projection и единственную ближайшую status/lifetime границу текущей ревизии.</summary>
    private void Changed(Actor actor, RequestKey key, string code)
    {
        StateVersion = Next(StateVersion);
        _events.Enqueue(new EntityChanged(_binding, key, actor.View.Actor, actor.View, code));
        Schedule(actor);
    }

    /// <summary>Старая запись deadline безопасно игнорируется после любой мутации.</summary>
    private void Schedule(Actor actor)
    {
        if (actor.Deadline is { } previous) _deadlines.Remove(previous);
        actor.Deadline = null;
        if (!actor.View.Alive) return;
        int? at = null;
        if (!actor.View.StunnedByTurn && actor.View.StunnedUntilDs > _now) at = actor.View.StunnedUntilDs;
        if (!actor.View.ShieldedByTurn && actor.View.ShieldUntilDs > _now) at = Math.Min(at ?? int.MaxValue, actor.View.ShieldUntilDs);
        // Bot lifetime/departure grace принадлежит его brain; оно не является безусловным удалением registry.
        if (actor.View.Kind != EntityKind.Bot && actor.View.ExpiresAtDs is { } expiry) at = Math.Min(at ?? int.MaxValue, expiry);
        if (at is { } deadlineAt)
        {
            actor.Deadline = new(actor.View.Actor, actor.View.Revision, deadlineAt);
            _deadlines.Add(actor.Deadline);
        }
    }

    /// <summary>Simulation time задаёт только внешний последовательный owner, никогда wall clock.</summary>
    private void AdvanceClock(int time)
    {
        if (time < _now) throw new ArgumentOutOfRangeException(nameof(time));
        _now = time;
    }
    /// <summary>Сложение сроков не переполняет signed simulation clock.</summary>
    private int DeadlineAfter(int duration) => (int)Math.Min(int.MaxValue, (long)_now + duration);
    /// <summary>Revision не возвращается к нулю после исчерпания identity.</summary>
    private static ulong Next(ulong value) => value < ulong.MaxValue ? value + 1 : throw new InvalidOperationException("Entity revision exhausted.");
    /// <summary>Единственный последовательный владелец исключает reentrant command/effect исполнение.</summary>
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy) throw new InvalidOperationException("Entity world is already executing.");
        _busy = true;
    }

    /// <summary>Полная валидация shape выполняется до domain admission.</summary>
    public static bool IsValidOperation(EntityOperation? operation) => operation is not null &&
        TransportFingerprint.ValidKey(operation.Key) && ValidActor(operation.Actor) && operation.ExpectedRevision != 0 && operation.Action switch
        {
            CreateEntity create => create.Definition is { } definition && definition.Actor == operation.Actor && Enum.IsDefined(definition.Kind) &&
                ValidNode(definition.Node) && FinitePositive(definition.MaxIntegrity) && ValidPersona(definition.Persona) &&
                NodeFactRules.IsValidText(definition.FactionId, 128) && OptionalText(definition.PrincipalId) && OptionalText(definition.ProfileId) && definition.ExpiresAtDs is null or >= 0,
            DeleteEntity deletion => TransportFingerprint.ValidToken(deletion.Code, 64),
            DamageEntity damage => FinitePositive(damage.Amount),
            HealEntity heal => FinitePositive(heal.Amount),
            StunEntity stun => stun.DurationDs > 0,
            ShieldEntity shield => double.IsFinite(shield.Percent) && shield.Percent is >= 0 and <= 100 && shield.DurationDs > 0,
            MoveEntity move => ValidNode(move.Destination),
            SetEntityPersona persona => ValidPersona(persona.Persona),
            SetEntityFaction faction => NodeFactRules.IsValidText(faction.FactionId, 128) && OptionalText(faction.PrincipalId),
            SetEntityCombatState status => double.IsFinite(status.ShieldFactor) && status.ShieldFactor is >= 0.1 and <= 1 &&
                status.ShieldUntilDs >= 0 && status.StunUntilDs >= 0,
            AdjustEntityTrace trace => double.IsFinite(trace.Delta),
            ControlEntity or ReadEntity or ClearEntityCombatGrace => true,
            _ => false
        };

    /// <summary>Подтверждение содержит только exact identity и результат, без native payload.</summary>
    public static bool IsValidResult(EntityEffectResult? result) => result is not null && ValidActor(result.Actor) &&
        NodeFactRules.IsValidText(result.BindingEpoch, 128) && NodeFactRules.IsValidText(result.EffectId, 256) && result.ResidenceRevision > 0;

    /// <summary>Semantic fingerprint покрывает все поля закрытого intent, включая поколения и nullable metadata.</summary>
    public static byte[]? Fingerprint(EntityOperation? operation)
    {
        if (!IsValidOperation(operation)) return null;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-ENTITY-1"); writer.Write(operation!.Key.Id); writer.Write(operation.Actor.Id); writer.Write(operation.Actor.Generation);
            writer.Write(operation.ExpectedRevision.HasValue); if (operation.ExpectedRevision is { } revision) writer.Write(revision);
            switch (operation.Action)
            {
                case CreateEntity create:
                    writer.Write((byte)1); var definition = create.Definition;
                    writer.Write((int)definition.Kind); WriteNode(writer, definition.Node); writer.Write(definition.MaxIntegrity); WritePersona(writer, definition.Persona);
                    writer.Write(definition.FactionId); WriteText(writer, definition.PrincipalId); WriteText(writer, definition.ProfileId); writer.Write(definition.Autonomous);
                    writer.Write(definition.ExpiresAtDs.HasValue); if (definition.ExpiresAtDs is { } expiry) writer.Write(expiry);
                    break;
                case DeleteEntity deletion: writer.Write((byte)2); writer.Write(deletion.Code); break;
                case DamageEntity damage: writer.Write((byte)3); writer.Write(damage.Amount); writer.Write(damage.IgnoreShield); break;
                case HealEntity heal: writer.Write((byte)4); writer.Write(heal.Amount); break;
                case StunEntity stun: writer.Write((byte)5); writer.Write(stun.DurationDs); break;
                case ShieldEntity shield: writer.Write((byte)6); writer.Write(shield.Percent); writer.Write(shield.DurationDs); break;
                case MoveEntity move: writer.Write((byte)7); WriteNode(writer, move.Destination); break;
                case ControlEntity control: writer.Write((byte)8); writer.Write(control.Autonomous); break;
                case SetEntityPersona persona: writer.Write((byte)9); WritePersona(writer, persona.Persona); break;
                case SetEntityFaction faction: writer.Write((byte)10); writer.Write(faction.FactionId); WriteText(writer, faction.PrincipalId); break;
                case ReadEntity: writer.Write((byte)11); break;
                case SetEntityCombatState status:
                    writer.Write((byte)12); writer.Write(status.ShieldFactor); writer.Write(status.ShieldUntilDs); writer.Write(status.StunUntilDs); break;
                case AdjustEntityTrace trace: writer.Write((byte)13); writer.Write(trace.Delta); break;
                case ClearEntityCombatGrace: writer.Write((byte)14); break;
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    private static bool ValidActor(ActorKey? key) => key is { Generation: > 0 } && NodeFactRules.IsValidText(key.Id, 128);
    private static bool ValidNode(NodeKey? key) => key is { Generation: > 0 } && NodeFactRules.IsValidText(key.NodeId, 128);
    private static bool OptionalText(string? value) => value is null || NodeFactRules.IsValidText(value, 128);
    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
    private static bool ValidPersona(EntityPersona? persona) => persona is not null && NodeFactRules.IsValidText(persona.Alias, 42) &&
        persona.Avatar is "cipher" or "ghost" or "moth" or "fox" or "eye" or "orb" &&
        persona.Accent is "cyan" or "mint" or "amber" or "rose" or "violet" or "silver" && persona.Tagline is { Length: <= 80 } &&
        !persona.Tagline.Any(char.IsControl) && persona.Theme is "ion" or "phosphor" or "amber" or "noir" or "synthwave";
    private static void WriteNode(BinaryWriter writer, NodeKey key) { writer.Write(key.NodeId); writer.Write(key.Generation); }
    private static void WriteText(BinaryWriter writer, string? text) { writer.Write(text is not null); if (text is not null) writer.Write(text); }
    private static void WritePersona(BinaryWriter writer, EntityPersona persona)
    { writer.Write(persona.Alias); writer.Write(persona.Avatar); writer.Write(persona.Accent); writer.Write(persona.Tagline); writer.Write(persona.Theme); }
}
