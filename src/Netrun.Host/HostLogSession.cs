using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using System.Diagnostics;

namespace Netrun.Host;

/// <summary>Проекция только диагностических scalar-полей без владения runtime, запросом или native payload.</summary>
internal sealed class HostLogSession(HostLog log, string connectionId, Epochs epochs)
{
    /// <summary>Последнее наблюдение предотвращает повтор одного state/error на каждом pulse.</summary>
    private (MirrorSyncState State, string? Topology, string? Communication)? _health;
    /// <summary>Wall time управляет только наблюдениями, а не игровой симуляцией.</summary>
    private long _lastTelemetry = Stopwatch.GetTimestamp();
    /// <summary>Алерт публикуется на переходе, чтобы не заполнять журнал одной сохраняющейся нагрузкой.</summary>
    private bool _pressured;

    /// <summary>Нет snapshot allocation, если экспорт отключён или срок ещё не наступил.</summary>
    internal bool TelemetryDue => log.IsEnabled(HostLogLevel.Information) && log.TelemetryInterval is { } interval &&
        Stopwatch.GetElapsedTime(_lastTelemetry) >= interval;

    /// <summary>Один постоянный снимок и алерты изменения pressure/recovery.</summary>
    internal void Telemetry(HostLogMetrics metrics)
    {
        _lastTelemetry = Stopwatch.GetTimestamp();
        log.TryWrite(new(HostLogLevel.Information, "runtime_metrics", connectionId, epochs, Metrics: metrics));
        var pressured = metrics.InputQueueFrames >= 4096 || metrics.OutputQueueFrames >= 4096 ||
            metrics.OutputUnacked >= 4096 || metrics.JournalBytes >= 64L * 1024 * 1024;
        if (pressured == _pressured) return;
        _pressured = pressured;
        log.TryWrite(new(pressured ? HostLogLevel.Warning : HostLogLevel.Information,
            pressured ? "runtime_pressure" : "runtime_recovered", connectionId, epochs, Metrics: metrics));
    }

    /// <summary>Общий causal контекст совпадает с идентификаторами в DM monitor и framed output.</summary>
    internal void Write(HostLogLevel level, string name, string? code = null, string? state = null) =>
        log.TryWrite(new(level, name, connectionId, epochs, Code: code, State: state));

    /// <summary>Команды в debug; отказ всегда виден на warning, без сериализации command/payload.</summary>
    internal void Applied(RuntimeInput input, RuntimeResult result)
    {
        var rejected = result.Apply.Status is not (ApplyStatus.Applied or ApplyStatus.Duplicate) ||
            result.ErrorCode is not null || result.Admission?.Status is AdmissionStatus.Rejected or AdmissionStatus.WouldBlock ||
            result.Action is { Accepted: false };
        var level = rejected ? HostLogLevel.Warning : HostLogLevel.Debug;
        if (!log.IsEnabled(level)) return;
        var request = input.Command switch
        {
            SubmitRequest submit => submit.Request.Key,
            RejectRequest reject => reject.Key,
            CancelRequest cancel => cancel.Key,
            DiscoverServices discover => discover.Request.Key,
            CancelDiscovery cancel => cancel.Key,
            QueryProjection projection => projection.Request.Key,
            CancelProjection cancel => cancel.Key,
            StartGraphSynchronization start => start.Key,
            ContinueGraphSynchronization next => next.Key,
            CancelGraphSynchronization cancel => cancel.Key,
            QueryAnchor anchor => anchor.Request.Key,
            QueryEntityNavigation navigation => navigation.Request.Key,
            CancelEntityNavigation navigation => navigation.Key,
            SubmitEntityOperation entity => entity.Operation.Key,
            FinishEntityEffect entity => entity.Key,
            _ => result.Admission?.Key
        };
        log.TryWrite(new(level, rejected ? "command_rejected" : "command_applied", connectionId, epochs,
            InputSequence: input.Sequence, RequestId: request?.Id,
            InvocationId: (input.Command as FinishInvocation)?.Result.InvocationId,
            Command: input.Command.GetType().Name,
            Code: result.ErrorCode ?? result.Admission?.Code ?? result.Action?.Code ?? result.Apply.Code,
            State: result.Apply.Status.ToString()));
    }

    /// <summary>Выходная корреляция снимается на handoff, до записи в socket и без дублирования при replay.</summary>
    internal HostLogEvent? DescribeEvent(TransportEvent entry)
    {
        var level = entry is RequestCompleted { State: RequestState.Failed or RequestState.TimedOut or RequestState.OutcomeUnknown } or
            Netrun.Core.Scheduling.CoreTaskCompleted { Success: false }
            ? HostLogLevel.Warning : HostLogLevel.Debug;
        if (!log.IsEnabled(level)) return null;
        return new(level, entry switch
        {
            ServiceInvocation => "invocation_issued",
            RequestCompleted => "request_completed",
            Netrun.Core.Entities.EntityNativeEffect => "entity_effect_issued",
            Netrun.Core.Entities.EntityChanged => "entity_changed",
            Netrun.Core.Scheduling.CoreTaskCompleted => "core_task_completed",
            _ => "projection_issued"
        }, connectionId, epochs, RequestId: entry.Key.Id,
            InvocationId: (entry as ServiceInvocation)?.InvocationId ?? (entry as Netrun.Core.Entities.EntityNativeEffect)?.EffectId,
            Code: entry switch
            {
                RequestCompleted request => request.Code,
                Netrun.Core.Entities.EntityChanged entity => entity.Code,
                Netrun.Core.Scheduling.CoreTaskCompleted task => task.Code,
                _ => null
            }, State: (entry as RequestCompleted)?.State.ToString());
    }

    /// <summary>Output sequence назначается после проверки границы кадра, включая отложенный entry.</summary>
    internal void Event(HostLogEvent? entry, ulong outputSequence)
    {
        if (entry is not null) log.TryWrite(entry with { OutputSequence = outputSequence });
    }

    /// <summary>Изменение здоровья видно независимо от opt-in live monitor; логические часы не продвигаются.</summary>
    internal void Health(MirrorSyncState state, string? topology, string? communication)
    {
        var next = (state, topology, communication);
        if (_health == next) return;
        _health = next;
        Write(topology is null && communication is null ? HostLogLevel.Information : HostLogLevel.Warning,
            "runtime_state", topology ?? communication, state.ToString());
    }
}
