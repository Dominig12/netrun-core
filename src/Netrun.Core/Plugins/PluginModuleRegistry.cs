using System.Collections.ObjectModel;
using Netrun.Core.Entities;

namespace Netrun.Core.Plugins;

/// <summary>Реестр фабрик на этапе composition; собственного task scheduler или потока у модов нет.</summary>
public sealed class PluginModuleRegistry
{
    private readonly Dictionary<string, INetrunModuleFactory> _factories = new(StringComparer.Ordinal);

    /// <summary>Read-only состав; mutable dictionary наружу не выдаётся.</summary>
    public IReadOnlyDictionary<string, INetrunModuleFactory> Factories => new ReadOnlyDictionary<string, INetrunModuleFactory>(_factories);

    /// <summary>Добавляет одну фабрику; внешний NetrunPluginRegistry закрывает вызов после Configure.</summary>
    public void Register(INetrunModuleFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        string id;
        try { id = factory.Id; }
        catch (Exception) { throw new PluginException("plugin_module_metadata_failed"); }
        if (!ValidToken(id)) throw new PluginException("plugin_module_invalid");
        if (!_factories.TryAdd(id, factory)) throw new PluginException("plugin_module_duplicate");
    }

    /// <summary>Создаёт изолированные сессии для одного binding; ошибка полностью откатывает уже созданные ресурсы.</summary>
    public ModuleSessionSet CreateSessions(ModuleContext context, ModuleLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        limits ??= new();
        if (limits.MaxEffectsPerStep is < 1 or > 4096 || limits.MaxProjectionsPerStep is < 1 or > 1024)
            throw new ArgumentException("Invalid module limits.", nameof(limits));
        var sessions = new Dictionary<string, IModuleSession>(StringComparer.Ordinal);
        try
        {
            foreach (var (id, factory) in _factories)
            {
                IModuleSession? session;
                try { session = factory.Create(context); }
                catch (Exception) { throw new PluginException("plugin_module_create_failed"); }
                if (session is null || sessions.Values.Any(existing => ReferenceEquals(existing, session)))
                    throw new PluginException("plugin_module_session_invalid");
                sessions.Add(id, session);
            }
            return new(sessions, limits);
        }
        catch
        {
            foreach (var session in sessions.Values.Reverse())
                try { session.Dispose(); } catch (Exception) { /* Сохраняем исходный безопасный код. */ }
            throw;
        }
    }

    /// <summary>Namespaced identifiers и operations имеют одинаковый безопасный token alphabet.</summary>
    internal static bool ValidToken(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

/// <summary>
/// Владеет binding-сессиями и выданными cooperative tasks. Ничего не планирует и не исполняет самостоятельно:
/// Start возвращает обёртку, которую единственный Core FIFO owner вызывает с явным бюджетом.
/// </summary>
public sealed class ModuleSessionSet : IDisposable
{
    private readonly Dictionary<string, IModuleSession> _sessions;
    private readonly HashSet<ModuleTask> _tasks = [];
    private readonly ModuleLimits _limits;
    private bool _disposed;

    /// <summary>Полный проверенный состав создаёт только composition registry.</summary>
    internal ModuleSessionSet(Dictionary<string, IModuleSession> sessions, ModuleLimits limits)
    { _sessions = sessions; _limits = limits; }

    /// <summary>Неизменяемый список модов binding, без доступа к mutable sessions.</summary>
    public ValueList<string> ModuleIds => new(_sessions.Keys, int.MaxValue);

    /// <summary>Проверяет адрес/shape и создаёт ровно одну task; request replay/producer fences ведёт центральный owner.</summary>
    public IModuleTask Start(ModuleRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request is null || request.Key is null || !NodeFactRules.IsValidText(request.Key.ProducerId, 128) || request.Key.Sequence == 0 ||
            !PluginModuleRegistry.ValidToken(request.ModuleId) || !PluginModuleRegistry.ValidToken(request.Operation) ||
            request.Payload is null || !request.Payload.Validate())
            throw new PluginException("plugin_module_request_invalid");
        if (!_sessions.TryGetValue(request.ModuleId, out var session)) throw new PluginException("plugin_module_unknown");
        IModuleTask? task;
        try { task = session.Start(request); }
        catch (Exception) { throw new PluginException("plugin_module_start_failed"); }
        if (task is null || _tasks.Any(active => ReferenceEquals(active.Inner, task)))
            throw new PluginException("plugin_module_task_invalid");
        var wrapper = new ModuleTask(task, _limits, _tasks);
        _tasks.Add(wrapper);
        return wrapper;
    }

    /// <summary>Отменяет task resources перед session state; ошибка одного расширения не прерывает остальную очистку.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failed = false;
        foreach (var task in _tasks.ToArray())
            try { task.Dispose(); } catch (Exception) { failed = true; }
        foreach (var session in _sessions.Values.Reverse())
            try { session.Dispose(); } catch (Exception) { failed = true; }
        _tasks.Clear(); _sessions.Clear();
        if (failed) throw new PluginException("plugin_module_dispose_failed");
    }

    /// <summary>Проверяет bounded continuation и гарантирует Dispose после terminal либо ошибки.</summary>
    private sealed class ModuleTask(IModuleTask inner, ModuleLimits limits, HashSet<ModuleTask> owner) : IModuleTask
    {
        private bool _disposed;
        /// <summary>Identity нужна только для отказа при reuse одной mutable task несколькими запросами.</summary>
        internal IModuleTask Inner => inner;

        /// <inheritdoc />
        public ModuleStep Advance(int workBudget)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(workBudget);
            ModuleStep step;
            try { step = inner.Advance(workBudget); }
            catch (Exception)
            { DisposeAfterFailure(); throw new PluginException("plugin_module_step_failed"); }
            if (!ValidStep(step, workBudget, limits))
            { DisposeAfterFailure(); throw new PluginException("plugin_module_step_invalid"); }
            if (step.Complete) Dispose();
            return step;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Remove(this);
            try { inner.Dispose(); }
            catch (Exception) { throw new PluginException("plugin_module_task_dispose_failed"); }
        }

        /// <summary>Исходная ошибка Advance имеет приоритет над возможной ошибкой освобождения мода.</summary>
        private void DisposeAfterFailure()
        { try { Dispose(); } catch (Exception) { /* Исходный код ошибки уже известен вызывающему owner. */ } }
    }

    /// <summary>Trusted мод всё равно не может обойти контракт work/output budgets или wire-safe typed effects.</summary>
    private static bool ValidStep(ModuleStep? step, int budget, ModuleLimits limits)
    {
        if (step is null || step.Work < 0 || step.Work > budget || !step.Complete && step.Result is not null ||
            !step.Complete && budget > 0 && step.Work == 0 || step.Effects?.Count > limits.MaxEffectsPerStep ||
            step.Projections?.Count > limits.MaxProjectionsPerStep ||
            step.Work == 0 && (step.Effects is { Count: > 0 } || step.Projections is { Count: > 0 })) return false;
        if (step.Effects is { } effects)
            foreach (var effect in effects)
                if (effect is null || effect.Key is null || !NodeFactRules.IsValidText(effect.Key.ProducerId, 128) || effect.Key.Sequence == 0 ||
                    effect.Actor is null || !NodeFactRules.IsValidText(effect.Actor.Id, 128) || effect.Actor.Generation == 0 ||
                    effect.Action is not (CreateEntity or DeleteEntity or DamageEntity or HealEntity or StunEntity or ShieldEntity or
                        MoveEntity or ControlEntity or SetEntityPersona or SetEntityFaction or ReadEntity or
                        SetEntityCombatState or AdjustEntityTrace or ClearEntityCombatGrace)) return false;
        try
        {
            var projections = new List<NetworkValue>();
            if (step.Projections is { } rows)
                foreach (var projection in rows)
                {
                    if (projection is null || !PluginModuleRegistry.ValidToken(projection.Topic) || projection.Data is null) return false;
                    projections.Add(NetworkValue.FromObject(new Dictionary<string, NetworkValue>
                    { ["topic"] = NetworkValue.FromText(projection.Topic), ["data"] = projection.Data }));
                }
            // Общий aggregate budget предотвращает 8 независимых payload по 256 KiB в одном шаге.
            return NetworkValue.FromObject(new Dictionary<string, NetworkValue>
            { ["result"] = step.Result ?? NetworkValue.Null, ["projections"] = NetworkValue.FromArray(projections) }).Validate();
        }
        catch (ArgumentException) { return false; }
    }
}
