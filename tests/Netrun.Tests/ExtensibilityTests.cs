using Netrun.Core;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет расширение модуля без изменения координатора и обхода его инвариантов.</summary>
public sealed class ExtensibilityTests
{
    /// <summary>Собственный тип факта потребителя библиотеки, не известный WorldMirror.</summary>
    private sealed record DeviceRegistered(NodeFact Fact) : WorldChange;

    /// <summary>Пример чистого адаптера внешнего контракта к полному факту узла.</summary>
    private sealed class DeviceRegisteredHandler : NodeChangeHandler<DeviceRegistered>
    {
        /// <summary>Не сохраняет mutable-состояние и не создаёт игровых эффектов.</summary>
        protected override NodeMutation Normalize(DeviceRegistered change) =>
            new(change.Fact.NodeId, change.Fact.Generation, change.Fact.Revision, change.Fact);
    }

    /// <summary>Неправильное расширение: metadata намерения не совпадает с фактом.</summary>
    private sealed class InvalidHandler : NodeChangeHandler<DeviceRegistered>
    {
        /// <summary>Нарушение должно обнаружиться ядром до публикации.</summary>
        protected override NodeMutation Normalize(DeviceRegistered change) => new("other-id", 1, 1, change.Fact);
    }

    /// <summary>Доверенный callback позволяет проверить ошибку интеграции с повторным входом.</summary>
    private sealed class CallbackHandler(Action callback) : NodeChangeHandler<DeviceRegistered>
    {
        /// <summary>Вызывает тестовый callback до выдачи намерения, то есть до публикации.</summary>
        protected override NodeMutation Normalize(DeviceRegistered change)
        {
            callback();
            return new(change.Fact.NodeId, change.Fact.Generation, change.Fact.Revision, change.Fact);
        }
    }

    /// <summary>Проверяет применение и dedup собственного типа через публичный контракт.</summary>
    [Fact]
    public void ConsumerCanRegisterANewTypedChangeWithoutModifyingMirror()
    {
        var epochs = new Epochs("world", "engine", "binding");
        IWorldMirror mirror = new WorldMirror(epochs, additionalHandlers: [new DeviceRegisteredHandler()]);
        var fact = new NodeFact("device", 1, 1, new("address", "server", new(1, 2, 1), "sector:1", true));
        var batch = new InputBatch(epochs, 1, 0, [new DeviceRegistered(fact)]);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(batch).Status);
        Assert.Equal(fact, mirror.Nodes["device"]);
        Assert.Equal(ApplyStatus.Duplicate, mirror.Apply(batch).Status);
    }

    /// <summary>Расширение не получает права обходить обязательную валидацию.</summary>
    [Fact]
    public void InvalidExtensionCannotBypassFactValidation()
    {
        var epochs = new Epochs("world", "engine", "binding");
        var mirror = new WorldMirror(epochs, additionalHandlers: [new InvalidHandler()]);
        var fact = new NodeFact("device", 1, 1, new("address", "server", new(1, 2, 1), "sector:1", true));
        Assert.Equal("invalid_batch", mirror.Apply(new(epochs, 1, 0, [new DeviceRegistered(fact)])).Code);
        Assert.Empty(mirror.Nodes);
    }

    /// <summary>Неизвестная запись отклоняется вместо молчаливого игнорирования.</summary>
    [Fact]
    public void UnregisteredChangeIsRejected()
    {
        var epochs = new Epochs("world", "engine", "binding");
        var mirror = new WorldMirror(epochs);
        var fact = new NodeFact("device", 1, 1, new("address", "server", new(1, 2, 1), "sector:1", true));
        Assert.Equal("unsupported_change", mirror.Apply(new(epochs, 1, 0, [new DeviceRegistered(fact)])).Code);
    }

    /// <summary>Неоднозначный выбор обработчика обнаруживается при composition, до входного потока.</summary>
    [Fact]
    public void DuplicateHandlerRegistrationFailsAtConstruction() =>
        Assert.Throws<ArgumentException>(() => new WorldMirror(new("world", "engine", "binding"),
            additionalHandlers: [new DeviceRegisteredHandler(), new DeviceRegisteredHandler()]));

    /// <summary>Replay сравнивает полный эффект, поэтому явный alias типа не создаёт второе применение.</summary>
    [Fact]
    public void EquivalentNormalizedEffectsHaveTheSameReplayIdentity()
    {
        var epochs = new Epochs("world", "engine", "binding");
        var mirror = new WorldMirror(epochs, additionalHandlers: [new DeviceRegisteredHandler()]);
        var fact = new NodeFact("device", 1, 1, new("address", "server", new(1, 2, 1), "sector:1", true));
        mirror.Apply(new(epochs, 1, 0, [new DeviceRegistered(fact)]));
        Assert.Equal(ApplyStatus.Duplicate, mirror.Apply(new(epochs, 1, 0, [new NodeUpsert(fact)])).Status);
    }

    /// <summary>Ошибка расширения не публикует пакет и не оставляет guard навсегда закрытым.</summary>
    [Fact]
    public void ReentrantHandlerCannotPartiallyPublishAnotherBatch()
    {
        var epochs = new Epochs("world", "engine", "binding");
        WorldMirror? mirror = null;
        mirror = new WorldMirror(epochs, additionalHandlers:
            [new CallbackHandler(() => mirror!.Apply(new(epochs, 1, 0, [])))]);
        var fact = new NodeFact("device", 1, 1, new("address", "server", new(1, 2, 1), "sector:1", true));
        Assert.Throws<InvalidOperationException>(() => mirror.Apply(new(epochs, 1, 0, [new DeviceRegistered(fact)])));
        Assert.Equal(0UL, mirror.AppliedSequence);
        Assert.Empty(mirror.Nodes);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(new(epochs, 1, 0, [new NodeUpsert(fact)])).Status);
    }
}
