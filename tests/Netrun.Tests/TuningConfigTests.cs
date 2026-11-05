using Netrun.Core.Entities.Bots;
using Netrun.Core.Entities.Combat;
using Netrun.Host;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет загрузку игрового тюнинга из JSON: частичные секции, штатные значения и отказ по диапазону.</summary>
public sealed class TuningConfigTests
{
    /// <summary>Пустой путь и пустой файл сохраняют штатную конфигурацию без опций.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingFileKeepsDefaults(string? json)
    {
        var (combat, bots) = TuningConfig.Parse(json);
        Assert.Null(combat);
        Assert.Null(bots);
    }

    /// <summary>Отсутствующие в JSON поля наследуют штатные значения записи-опций.</summary>
    [Fact]
    public void PartialCombatInheritsDefaults()
    {
        var (combat, bots) = TuningConfig.Parse("""{ "combat": { "decisionTimeDs": 200 } }""");
        Assert.NotNull(combat);
        Assert.Equal(200, combat!.DecisionTimeDs);
        Assert.Equal(3, combat.IdleRounds);
        Assert.Equal(5, combat.TelegraphInCombatDs);
        Assert.Equal(1000, combat.MaximumDamageScale);
        Assert.Null(bots);
    }

    /// <summary>Вложенные секции тюнинга бота разбираются, а опущенные группы остаются штатными.</summary>
    [Fact]
    public void NestedBotTuningParses()
    {
        var (combat, bots) = TuningConfig.Parse(
            """{ "bots": { "maxBots": 64, "tuning": { "goals": { "hack": 5000 }, "maxActiveInvasions": 4 } } }""");
        Assert.Null(combat);
        Assert.NotNull(bots);
        Assert.Equal(64, bots!.MaxBots);
        Assert.NotNull(bots.Tuning);
        Assert.Equal(5000, bots.Tuning!.Goals!.Hack);
        Assert.Equal(300, bots.Tuning.Goals.Guard);         // опущено → штатное
        Assert.Equal(4, bots.Tuning.MaxActiveInvasions);
        Assert.Null(bots.Tuning.Memory);                    // целая группа опущена
    }

    /// <summary>Значение вне допустимого диапазона отвергается сразу при старте.</summary>
    [Fact]
    public void OutOfRangeCombatRejectedEagerly() =>
        Assert.Throws<ArgumentException>(() => TuningConfig.Parse("""{ "combat": { "decisionTimeDs": 0 } }"""));

    /// <summary>Недопустимый тюнинг бота отвергается до первого bind.</summary>
    [Fact]
    public void OutOfRangeBotTuningRejectedEagerly() =>
        Assert.Throws<ArgumentException>(() =>
            TuningConfig.Parse("""{ "bots": { "tuning": { "sabotageHoldDs": -1 } } }"""));

    /// <summary>Повреждённый JSON превращается в ArgumentException, а не в необработанное исключение.</summary>
    [Fact]
    public void MalformedJsonRejected() =>
        Assert.Throws<ArgumentException>(() => TuningConfig.Parse("{ not json"));

    /// <summary>FromEnvironment читает путь и файл через инъекции, без обращения к диску.</summary>
    [Fact]
    public void FromEnvironmentReadsInjectedFile()
    {
        var (combat, _) = TuningConfig.FromEnvironment(
            _ => "tuning.json", _ => """{ "combat": { "idleRounds": 7 } }""");
        Assert.NotNull(combat);
        Assert.Equal(7, combat!.IdleRounds);
    }
}
