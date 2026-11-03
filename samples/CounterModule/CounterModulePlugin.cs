using Netrun.Core;
using Netrun.Core.Plugins;

namespace Netrun.Samples.CounterModule;

/// <summary>Минимальный stateful мод: изолированный счётчик и собственная UI projection без native кода.</summary>
public sealed class CounterModulePlugin : INetrunPlugin
{
    /// <inheritdoc />
    public PluginMetadata Metadata { get; } = new("example.counter", "Counter Module", "1.0.0");
    /// <inheritdoc />
    public void Configure(INetrunPluginRegistry registry) => registry.RegisterModule(new CounterFactory());

    /// <summary>Factory immutable; значение счётчика хранится только внутри binding-сессии.</summary>
    private sealed class CounterFactory : INetrunModuleFactory
    {
        /// <inheritdoc />
        public string Id => "example.counter";
        /// <inheritdoc />
        public IModuleSession Create(ModuleContext context) => new CounterSession();
    }

    /// <summary>Один worker владеет состоянием конкретного binding.</summary>
    private sealed class CounterSession : IModuleSession
    {
        private ulong _count;
        /// <inheritdoc />
        public IModuleTask Start(ModuleRequest request)
        {
            if (request.Operation != "increment" || request.Payload is not NetworkNumber { Value: >= 1 and <= 10000 } amount ||
                amount.Value != Math.Truncate(amount.Value)) throw new InvalidOperationException("counter_request_invalid");
            return new IncrementTask(this, (int)amount.Value);
        }
        /// <inheritdoc />
        public void Dispose() { }

        /// <summary>Каждое продолжение потребляет явный бюджет и не блокирует очередь циклом до готовности.</summary>
        private sealed class IncrementTask(CounterSession session, int remaining) : IModuleTask
        {
            /// <inheritdoc />
            public ModuleStep Advance(int workBudget)
            {
                var work = Math.Min(remaining, workBudget);
                remaining -= work;
                session._count += (uint)work;
                if (remaining > 0) return new(work, false);
                var value = NetworkValue.FromText(session._count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return new(work, true, value, Projections: new([new("counter", value)]));
            }
            /// <inheritdoc />
            public void Dispose() { }
        }
    }
}
