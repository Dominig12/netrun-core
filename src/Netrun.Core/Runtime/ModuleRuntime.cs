using Netrun.Core.Plugins;
using Netrun.Core.Scheduling;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

public sealed partial class NetworkRuntime
{
    /// <summary>Изолированные mutable mod sessions одного binding.</summary>
    private ModuleSessionSet? _moduleSessions;
    /// <summary>Только bounded immutable результаты/projections, не native actions.</summary>
    private readonly Queue<TransportEvent> _moduleEvents = [];
    /// <summary>Ошибка освобождения расширения сохраняется для диагностики, не прерывая cleanup остальных owners.</summary>
    public string? LastPluginError { get; private set; }
    /// <summary>Состав модов текущего binding; factory sessions не разделяют mutable state между мирами.</summary>
    public IReadOnlyList<string> Modules => _moduleSessions?.ModuleIds ?? new ValueList<string>([]);

    /// <summary>Module Step и его effects исполняются внутри текущего FIFO head, до следующей задачи.</summary>
    private CoreTaskStep ExecuteModuleTask(EntityWork work, ModuleRequest request, int budget)
    {
        var consumed = 0;
        try
        {
            if (_moduleEvents.Count >= (_options.Tasks?.MaximumEvents ?? int.MaxValue)) return new(0, false);
            work.ModuleTask ??= _moduleSessions!.Start(request);
            if (work.ModuleStep is null)
            {
                work.ModuleStep = work.ModuleTask.Advance(budget);
                consumed = work.ModuleStep.Work;
                work.EffectCursor = work.ProjectionCursor = 0;
            }
            var step = work.ModuleStep;
            while (step.Effects is { } effects && work.EffectCursor < effects.Count && consumed < budget)
            {
                var admission = _entities!.Submit(effects[work.EffectCursor], SimulationTimeDs);
                if (admission.Status == AdmissionStatus.WouldBlock) return new(consumed, false);
                if (admission.Status is not (AdmissionStatus.Accepted or AdmissionStatus.Duplicate))
                    return FailModuleTask(work, request, "plugin_module_effect_rejected", Math.Max(1, consumed));
                work.EffectCursor++; consumed++;
            }
            if (step.Effects is { } remainingEffects && work.EffectCursor < remainingEffects.Count) return new(consumed, false);
            while (step.Projections is { } projections && work.ProjectionCursor < projections.Count && consumed < budget)
            {
                if (_moduleEvents.Count >= (_options.Tasks?.MaximumEvents ?? int.MaxValue)) return new(consumed, false);
                var projection = projections[work.ProjectionCursor++];
                _moduleEvents.Enqueue(new ModuleProjectionChanged(_epochs.Binding, request.Key, request.ModuleId, projection.Topic, projection.Data));
                consumed++;
            }
            if (step.Projections is { } remainingProjections && work.ProjectionCursor < remainingProjections.Count) return new(consumed, false);
            if (step.Complete)
            {
                if (_moduleEvents.Count >= (_options.Tasks?.MaximumEvents ?? int.MaxValue)) return new(consumed, false);
                _moduleEvents.Enqueue(new ModuleCompleted(_epochs.Binding, request.Key, request.ModuleId, true, "completed", step.Result ?? NetworkValue.Null));
                work.ModuleStep = null;
                return new(Math.Max(1, consumed), true);
            }
            work.ModuleStep = null;
            return new(consumed, false);
        }
        catch (PluginException failure) { return FailModuleTask(work, request, failure.Code, Math.Max(1, consumed)); }
    }

    /// <summary>Отказ мода не повторяет ни его Step, ни уже выданные typed mutations.</summary>
    private CoreTaskStep FailModuleTask(EntityWork work, ModuleRequest request, string code, int consumed)
    {
        LastPluginError = code;
        try { work.ModuleTask?.Dispose(); }
        catch (PluginException) { /* Исходная безопасная причина приоритетнее cleanup ошибки. */ }
        work.ModuleTask = null;
        work.ModuleStep = null;
        _moduleEvents.Enqueue(new ModuleCompleted(_epochs.Binding, request.Key, request.ModuleId, false, code, NetworkValue.Null));
        return new(consumed, true, code, false);
    }
}
