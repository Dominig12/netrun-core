using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Plugins;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет per-binding mods, cooperative budgets, typed effects, rollback и безопасное освобождение.</summary>
public sealed class PluginModuleTests
{
    private static readonly ActorKey Actor = new("actor", 1);
    private static readonly NodeKey Node = new("node", 1);

    /// <summary>Read-only capabilities связаны с одним simulation owner.</summary>
    private static ModuleContext Context(string binding = "binding") => new(binding, _ => null, () => 0);
    /// <summary>Обычная producer identity внешнего запроса не заменяется случайным GUID.</summary>
    private static ModuleRequest Request(string module = "example.counter", ulong sequence = 1) =>
        new(new("producer", sequence), module, "count", NetworkValue.FromNumber(2));

    /// <summary>Factory регистрируется через тот же sealed plugin composition registry.</summary>
    [Fact]
    public void PluginRegistersModuleAndCannotMutateRegistryAfterConfigure()
    {
        var plugin = new CounterPlugin();
        var registry = NetrunPluginRegistry.Configure([plugin]);
        Assert.Single(registry.Modules.Factories);
        Assert.Equal("example.counter", Assert.Single(registry.Modules.Factories).Key);
        var error = Assert.Throws<PluginException>(() => plugin.Captured!.RegisterModule(new CounterFactory("late.module")));
        Assert.Equal("plugin_registry_closed", error.Code);
    }

    /// <summary>Повтор Id/неверный token обнаруживается до первого входа runtime.</summary>
    [Fact]
    public void DuplicateAndInvalidModuleIdsAreRejected()
    {
        var registry = new PluginModuleRegistry();
        registry.Register(new CounterFactory());
        Assert.Equal("plugin_module_duplicate", Assert.Throws<PluginException>(() => registry.Register(new CounterFactory())).Code);
        Assert.Equal("plugin_module_invalid", Assert.Throws<PluginException>(() => registry.Register(new CounterFactory("Invalid Id"))).Code);
        var view = Assert.IsAssignableFrom<IDictionary<string, INetrunModuleFactory>>(registry.Factories);
        Assert.Throws<NotSupportedException>(() => view.Clear());
    }

    /// <summary>Состояние разных binding не разделяется даже при одном загруженном экземпляре factory.</summary>
    [Fact]
    public void SessionsAreIndependentAndTasksYieldCooperatively()
    {
        var registry = new PluginModuleRegistry();
        registry.Register(new CounterFactory());
        using var first = registry.CreateSessions(Context("first"));
        using var second = registry.CreateSessions(Context("second"));
        using var firstTask = first.Start(Request());
        var one = firstTask.Advance(1);
        Assert.Equal(1, one.Work);
        Assert.False(one.Complete);
        using var secondTask = second.Start(Request() with { Payload = NetworkValue.FromNumber(1) });
        Assert.Equal(1, Assert.IsType<NetworkNumber>(secondTask.Advance(1).Result).Value);
        Assert.Equal(2, Assert.IsType<NetworkNumber>(firstTask.Advance(1).Result).Value);
        Assert.Throws<ObjectDisposedException>(() => firstTask.Advance(1));
        using var next = first.Start(Request(sequence: 2) with { Payload = NetworkValue.FromNumber(1) });
        Assert.Equal(3, Assert.IsType<NetworkNumber>(next.Advance(1).Result).Value);
    }

    /// <summary>Create failure освобождает уже созданные sessions и скрывает пользовательский текст исключения.</summary>
    [Fact]
    public void SessionCreationFailureRollsBackPreviousSessions()
    {
        var disposed = 0;
        var registry = new PluginModuleRegistry();
        registry.Register(new Factory("first.module", _ => new Session(_ => new Task(_ => new(0, true)), () => disposed++)));
        registry.Register(new Factory("failed.module", _ => throw new Exception("private-module-detail")));
        var error = Assert.Throws<PluginException>(() => registry.CreateSessions(Context()));
        Assert.Equal("plugin_module_create_failed", error.Code);
        Assert.Equal(1, disposed);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-", error.ToString());
    }

    /// <summary>Unknown/invalid requests не попадают в мод и не расходуют его state.</summary>
    [Fact]
    public void RequestShapeAndModuleAddressAreValidated()
    {
        var starts = 0;
        var registry = new PluginModuleRegistry();
        registry.Register(new Factory("example.counter", _ => new Session(_ => { starts++; return new Task(_ => new(0, true)); })));
        using var sessions = registry.CreateSessions(Context());
        Assert.Equal("plugin_module_unknown", Assert.Throws<PluginException>(() => sessions.Start(Request("unknown.module"))).Code);
        Assert.Equal("plugin_module_request_invalid", Assert.Throws<PluginException>(() => sessions.Start(Request() with { Operation = "bad command" })).Code);
        Assert.Equal("plugin_module_request_invalid", Assert.Throws<PluginException>(() => sessions.Start(Request() with { Key = new("producer", 0) })).Code);
        Assert.Equal(0, starts);
    }

    /// <summary>Over-budget, zero-progress и premature result прекращают task без частичного handoff output.</summary>
    [Theory]
    [InlineData("budget")]
    [InlineData("progress")]
    [InlineData("result")]
    [InlineData("effects")]
    [InlineData("projection")]
    public void InvalidStepIsRejectedAndDisposed(string kind)
    {
        var disposed = 0;
        ModuleStep Step(int budget) => kind switch
        {
            "budget" => new(budget + 1, false),
            "progress" => new(0, false),
            "result" => new(1, false, NetworkValue.FromNumber(1)),
            "effects" => new(1, true, Effects: new(Enumerable.Range(1, 3).Select(index =>
                new EntityOperation(new("module", (ulong)index), Actor, 1, new HealEntity(1))))),
            _ => new(1, true, Projections: new(Enumerable.Range(0, 5).Select(_ =>
                new ModuleProjection("large", NetworkValue.FromText(new string('a', 60_000))))))
        };
        var registry = new PluginModuleRegistry();
        registry.Register(new Factory("example.counter", _ => new Session(_ => new Task(Step, () => disposed++))));
        using var sessions = registry.CreateSessions(Context(), new(MaxEffectsPerStep: 2));
        using var task = sessions.Start(Request());
        Assert.Equal("plugin_module_step_invalid", Assert.Throws<PluginException>(() => task.Advance(1)).Code);
        Assert.Equal(1, disposed);
        Assert.Throws<ObjectDisposedException>(() => task.Advance(1));
    }

    /// <summary>Мод только возвращает typed mutation; изменяет actor исключительно общий EntityWorld owner.</summary>
    [Fact]
    public void TypedEffectsAreReturnedWithoutExecutingGameOrEntityMutation()
    {
        using var world = new EntityWorld("binding");
        world.Submit(new(new("setup", 1), Actor, null,
            new CreateEntity(new(Actor, EntityKind.Bot, Node, 100, new("Actor"), "owner"))), 0);
        var registry = new PluginModuleRegistry();
        registry.Register(new Factory("example.damage", _ => new Session(_ => new Task(_ =>
            new(1, true, Effects: new([new(new("module", 1), Actor, 1, new DamageEntity(10))]))))));
        using var sessions = registry.CreateSessions(new("binding", world.Read, () => 0));
        using var task = sessions.Start(Request("example.damage"));
        var step = task.Advance(1);
        Assert.Equal(100, world.Read(Actor)!.Integrity);
        var operation = Assert.Single(step.Effects!);
        Assert.Equal(AdmissionStatus.Accepted, world.Submit(operation, 0).Status);
        Assert.Equal(90, world.Read(Actor)!.Integrity);
    }

    /// <summary>Сохранённые callback возможности читают актуальный immutable actor snapshot и simulation clock.</summary>
    [Fact]
    public void ModuleContextExposesOnlyReadModelsAndLogicalClock()
    {
        var now = 5;
        using var world = new EntityWorld("binding");
        world.Submit(new(new("setup", 1), Actor, null,
            new CreateEntity(new(Actor, EntityKind.Bot, Node, 100, new("Actor"), "owner"))), 0);
        var context = new ModuleContext("binding", world.Read, () => now);
        Assert.Equal("binding", context.BindingEpoch);
        Assert.Equal(5, context.SimulationTimeDs);
        var before = context.ReadEntity(Actor)!;
        world.Submit(new(new("damage", 1), Actor, before.Revision, new DamageEntity(5)), 5);
        now = 8;
        Assert.Equal(8, context.SimulationTimeDs);
        Assert.Equal(100, before.Integrity);
        Assert.Equal(95, context.ReadEntity(Actor)!.Integrity);
    }

    /// <summary>Shutdown освобождает unfinished task перед session; повторное Dispose ничего не повторяет.</summary>
    [Fact]
    public void DisposeReleasesActiveTasksBeforeSessionState()
    {
        var order = new List<string>();
        var registry = new PluginModuleRegistry();
        registry.Register(new Factory("example.counter", _ => new Session(_ => new Task(_ => new(1, false),
            () => order.Add("task")), () => order.Add("session"))));
        var sessions = registry.CreateSessions(Context());
        var task = sessions.Start(Request());
        task.Advance(1);
        sessions.Dispose(); sessions.Dispose(); task.Dispose();
        Assert.Equal(new[] { "task", "session" }, order);
        Assert.Throws<ObjectDisposedException>(() => sessions.Start(Request()));
    }

    /// <summary>Исключение cooperative кода не раскрывается и не оставляет task живой.</summary>
    [Fact]
    public void ModuleExceptionsHaveStableSafeCodes()
    {
        var disposed = 0;
        var registry = new PluginModuleRegistry();
        registry.Register(new Factory("example.counter", _ => new Session(_ => new Task(_ => throw new Exception("private-payload"), () => disposed++))));
        using var sessions = registry.CreateSessions(Context());
        using var task = sessions.Start(Request());
        var error = Assert.Throws<PluginException>(() => task.Advance(1));
        Assert.Equal("plugin_module_step_failed", error.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(1, disposed);
    }

    private sealed class CounterPlugin : INetrunPlugin
    {
        public PluginMetadata Metadata => new("example", "Example", "1");
        public INetrunPluginRegistry? Captured;
        public void Configure(INetrunPluginRegistry registry)
        { Captured = registry; registry.RegisterModule(new CounterFactory()); }
    }

    private sealed class CounterFactory(string id = "example.counter") : INetrunModuleFactory
    {
        public string Id => id;
        public IModuleSession Create(ModuleContext context) => new CounterSession();
    }

    private sealed class CounterSession : IModuleSession
    {
        private int _count;
        public IModuleTask Start(ModuleRequest request)
        {
            var left = (int)((NetworkNumber)request.Payload).Value;
            return new Task(budget =>
            {
                var work = Math.Min(left, budget);
                left -= work; _count += work;
                return new(work, left == 0, left == 0 ? NetworkValue.FromNumber(_count) : null);
            });
        }
        public void Dispose() { }
    }

    private sealed class Factory(string id, Func<ModuleContext, IModuleSession> create) : INetrunModuleFactory
    {
        public string Id => id;
        public IModuleSession Create(ModuleContext context) => create(context);
    }

    private sealed class Session(Func<ModuleRequest, IModuleTask> start, Action? dispose = null) : IModuleSession
    {
        public IModuleTask Start(ModuleRequest request) => start(request);
        public void Dispose() => dispose?.Invoke();
    }

    private sealed class Task(Func<int, ModuleStep> advance, Action? dispose = null) : IModuleTask
    {
        public ModuleStep Advance(int budget) => advance(budget);
        public void Dispose() => dispose?.Invoke();
    }
}
