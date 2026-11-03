using Netrun.Core.Entities;
using Netrun.Core.Transport;

namespace Netrun.Core.Plugins;

/// <summary>Доверенный мод создаёт отдельное состояние на binding; factory не хранит изменяемое session-состояние.</summary>
public interface INetrunModuleFactory
{
    /// <summary>Стабильный namespaced token, например example.patrol; уникален во всём composition.</summary>
    string Id { get; }
    /// <summary>Создаёт изолированную сессию мода без I/O и доступа к mutable владельцам ядра.</summary>
    IModuleSession Create(ModuleContext context);
}

/// <summary>Изменяемое состояние одного мода принадлежит ровно одному binding и одному Core worker.</summary>
public interface IModuleSession : IDisposable
{
    /// <summary>Создаёт cooperative task; тяжёлые вычисления выполняются только через её Advance.</summary>
    IModuleTask Start(ModuleRequest request);
}

/// <summary>Cooperative единица продолжительной работы, исполняемая общей FIFO Core очередью.</summary>
public interface IModuleTask : IDisposable
{
    /// <summary>Делает максимум workBudget единиц; возвращает immutable результат и typed эффекты без исполнения mutation.</summary>
    ModuleStep Advance(int workBudget);
}

/// <summary>Неизменяемый запрос модуля с обычной producer identity и bounded типизированным payload.</summary>
public sealed record ModuleRequest(RequestKey Key, string ModuleId, string Operation, NetworkValue Payload);

/// <summary>UI/read-model публикация мода, не произвольный native callback или имя DM proc.</summary>
public sealed record ModuleProjection(string Topic, NetworkValue Data);

/// <summary>
/// Одно cooperative продолжение. Effects применяет общий Core owner в порядке списка до следующей FIFO задачи.
/// Result допустим только при Complete; промежуточные projections ограничены общим encoded budget.
/// </summary>
public sealed record ModuleStep(int Work, bool Complete, NetworkValue? Result = null,
    ValueList<EntityOperation>? Effects = null, ValueList<ModuleProjection>? Projections = null);

/// <summary>Read-only возможности сессии. Снимки EntityWorld immutable, логическое время не является wall-clock.</summary>
public sealed class ModuleContext
{
    private readonly Func<ActorKey, EntityView?> _readEntity;
    private readonly Func<int> _simulationTimeDs;

    /// <summary>Фиксирует binding и read-only callbacks единственного Core owner.</summary>
    public ModuleContext(string bindingEpoch, Func<ActorKey, EntityView?> readEntity, Func<int> simulationTimeDs)
    {
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        ArgumentNullException.ThrowIfNull(readEntity);
        ArgumentNullException.ThrowIfNull(simulationTimeDs);
        BindingEpoch = bindingEpoch;
        _readEntity = readEntity;
        _simulationTimeDs = simulationTimeDs;
    }

    /// <summary>Идентичность сессии; состояние другого binding через этот context не доступно.</summary>
    public string BindingEpoch { get; }
    /// <summary>Текущее simulation DS единственного Core owner.</summary>
    public int SimulationTimeDs => _simulationTimeDs();
    /// <summary>Снимок exact actor generation; изменения выполняются только возвращаемыми EntityOperation.</summary>
    public EntityView? ReadEntity(ActorKey actor) => _readEntity(actor);
}

/// <summary>Локальные пределы одного cooperative шага; глобальные task/output limits принадлежат FIFO scheduler.</summary>
public sealed record ModuleLimits(int MaxEffectsPerStep = 32, int MaxProjectionsPerStep = 8);
