namespace Netrun.Core.Entities.Combat;

/// <summary>Общая immutable metadata и полиморфные Apply/status hooks; базовая программа не имеет эффекта.</summary>
public record CombatProgram
{
    /// <summary>Стабильный ключ реестра.</summary>
    public required string Id { get; init; }
    /// <summary>Тактическая metadata; не выбирает реализацию эффекта.</summary>
    public string Role { get; init; } = "attack";
    /// <summary>Базовая сила конкретного эффекта.</summary>
    public double Power { get; init; } = 20;
    /// <summary>Стоимость от одного до трёх AP.</summary>
    public int ApCost { get; init; } = 2;
    /// <summary>Cooldown в собственных ходах.</summary>
    public int CooldownTurns { get; init; }
    /// <summary>Cooldown вне encounter в децисекундах.</summary>
    public int CooldownDs { get; init; } = 20;
    /// <summary>Подготовка первого ICE-нападения в децисекундах.</summary>
    public int WindupDs { get; init; } = 25;
    /// <summary>Число собственных ходов щита.</summary>
    public int ShieldTurns { get; init; } = 1;
    /// <summary>Число пропускаемых ходов.</summary>
    public int StunTurns { get; init; } = 1;
    /// <summary>Расходуется один настоящий executable.</summary>
    public bool Consumable { get; init; }
    /// <summary>Требуется отдельная цель; иначе программа применяется к Source.</summary>
    public bool NeedsTarget { get; init; } = true;
    /// <summary>Принятое действие считается враждебной активностью.</summary>
    public bool Hostile { get; init; }
    /// <summary>Контртрассировка тоже обнуляет quiet-round ledger.</summary>
    public bool TargetsBreach { get; init; }
    /// <summary>Нужен ли native neural feedback при реальном эффекте.</summary>
    public bool Biofeedback { get; init; }
    /// <summary>Разрешён запуск entity-консолью.</summary>
    public bool ConsoleUsable { get; init; } = true;
    /// <summary>След при entity-запуске.</summary>
    public int Trace { get; init; } = 3;

    /// <summary>Чистая проверка статуса до оплаты AP/cooldown/расходника.</summary>
    public virtual bool CanApply(CombatProgramContext context) => true;
    /// <summary>Тактическая стоимость без изменения эффекта или общей metadata.</summary>
    public virtual double TurnValue(double effectiveLimit) => Power;
    /// <summary>Точная оценка DM: вне боя Power, в очереди полезность за AP со скидкой расходника.</summary>
    public double BotValue(double effectiveLimit, bool turnBased) => turnBased
        ? TurnValue(effectiveLimit) / Math.Max(1, ApCost) * (Consumable ? 0.8 : 1) : Power;
    /// <summary>Базовая программа ничего не делает независимо от значения Role.</summary>
    public virtual CombatProgramApplication Apply(CombatProgramContext context) => new(new([]));
}

/// <summary>Урон: BYOND round положительной силы округляет вниз, shield scaling остаётся дробным.</summary>
public sealed record AttackCombatProgram : CombatProgram
{
    /// <summary>Перебор лишнего урона не повышает полезность.</summary>
    public override double TurnValue(double effectiveLimit) => Math.Min(Power, effectiveLimit);
    /// <summary>Масштабируется только конкретный cast; shared definition остаётся immutable.</summary>
    public override CombatProgramApplication Apply(CombatProgramContext context) => new(new([
        new(CombatEffectKind.Damage, context.Target.Key, context.Source,
            Math.Floor(Power * context.DamageScale) * context.ActiveShieldFactor, ProgramId: Id, Biofeedback: Biofeedback)
    ]));
}

/// <summary>Подавление со stun immunity до окончания следующей полноценной возможности действовать.</summary>
public sealed record StunCombatProgram : CombatProgram
{
    /// <summary>Подавление оценивается количеством пропускаемых ходов.</summary>
    public override double TurnValue(double effectiveLimit) => 20 * Math.Max(1, StunTurns);
    /// <summary>Не разрешает продлевать цепочку пропусков до оплаты.</summary>
    public override bool CanApply(CombatProgramContext context) => !context.TurnBased || !context.TargetStunImmune;
    /// <summary>Вне боя действует конечный wall-clock статус; encounter принимает ownership собственных ходов.</summary>
    public override CombatProgramApplication Apply(CombatProgramContext context) => new(new([
        new(CombatEffectKind.Stun, context.Target.Key, context.Source, UntilDs: context.NowDs + (long)Power, ProgramId: Id)
    ]), context.TurnBased ? new(StunTurns: StunTurns) : null);
}

/// <summary>Защита не заменяет и не продлевает более сильный действующий щит.</summary>
public sealed record ShieldCombatProgram : CombatProgram
{
    /// <summary>Защита оценивается силой и длительностью в ходах.</summary>
    public override double TurnValue(double effectiveLimit) => Power * Math.Max(1, ShieldTurns);
    /// <summary>Равный щит разрешён; меньший factor соответствует более сильному щиту.</summary>
    public override bool CanApply(CombatProgramContext context) => !context.TargetShielded ||
        context.ActiveShieldFactor >= Math.Clamp(1 - Power / 100, 0.1, 1);
    /// <summary>Пять секунд вне очереди, явные собственные ходы внутри неё.</summary>
    public override CombatProgramApplication Apply(CombatProgramContext context)
    {
        var factor = Math.Clamp(1 - Power / 100, 0.1, 1);
        return new(new([
            new(CombatEffectKind.Shield, context.Target.Key, context.Source, factor, context.NowDs + 50, Id)
        ]), context.TurnBased ? new(factor, ShieldTurns) : null);
    }
}

/// <summary>Ремонт сохраняет силу при ICE damage scaling; registry ограничивает HP собственным максимумом.</summary>
public sealed record RepairCombatProgram : CombatProgram
{
    /// <summary>Избыточное лечение не увеличивает оценку.</summary>
    public override double TurnValue(double effectiveLimit) => Math.Min(Power, effectiveLimit);
    /// <summary>Не выполняет native callbacks и не мутирует общий actor registry.</summary>
    public override CombatProgramApplication Apply(CombatProgramContext context) => new(new([
        new(CombatEffectKind.Heal, context.Target.Key, context.Source, Power, ProgramId: Id)
    ]));
}

/// <summary>Контртрассировка изменяет trace действующего breach-владельца.</summary>
public sealed record TraceCombatProgram : CombatProgram
{
    /// <summary>Значение не зависит от damage scaling.</summary>
    public override CombatProgramApplication Apply(CombatProgramContext context) => new(new([
        new(CombatEffectKind.Trace, context.Target.Key, context.Source, Power, ProgramId: Id)
    ]));
}

/// <summary>Точные определения core/programs.dm; создание новых экземпляров не разделяет mutable cast state.</summary>
public static class CombatPrograms
{
    /// <summary>Все 19 штатных executable, включая ICE-only trace и базовые встроенные действия.</summary>
    public static ValueList<CombatProgram> CreateDefaults() => new([
        new AttackCombatProgram { Id = "sting", Power = 8, ApCost = 1, Hostile = true, Biofeedback = true, Trace = 2 },
        new AttackCombatProgram { Id = "pulse", Power = 18, Hostile = true, Biofeedback = true, Trace = 2 },
        new AttackCombatProgram { Id = "surge", Power = 28, ApCost = 3, Hostile = true, Biofeedback = true, Trace = 4 },
        new ShieldCombatProgram { Id = "shell", Role = "shield", Power = 25, ApCost = 1, CooldownTurns = 1, CooldownDs = 120, NeedsTarget = false, Trace = 1 },
        new RepairCombatProgram { Id = "mend", Role = "repair", Power = 8, ApCost = 1, CooldownTurns = 1, CooldownDs = 80, NeedsTarget = false, Trace = 0 },
        new AttackCombatProgram { Id = "strike", Power = 22, Hostile = true, Biofeedback = true },
        new AttackCombatProgram { Id = "assault", Power = 40, ApCost = 3, CooldownTurns = 1, CooldownDs = 50, Hostile = true, Biofeedback = true, Trace = 7 },
        new AttackCombatProgram { Id = "logic_bomb", Power = 70, ApCost = 3, CooldownDs = 10, Consumable = true, Hostile = true, Biofeedback = true, Trace = 10 },
        new StunCombatProgram { Id = "blackout", Role = "stun", Power = 40, CooldownTurns = 2, CooldownDs = 80, Hostile = true, Trace = 4 },
        new StunCombatProgram { Id = "emp", Role = "stun", Power = 60, ApCost = 3, StunTurns = 2, CooldownDs = 10, Consumable = true, Hostile = true, Trace = 8 },
        new ShieldCombatProgram { Id = "barrier", Role = "shield", Power = 50, CooldownTurns = 1, CooldownDs = 100, NeedsTarget = false, Trace = 2 },
        new ShieldCombatProgram { Id = "fortify", Role = "shield", Power = 65, ApCost = 3, CooldownTurns = 2, ShieldTurns = 2, CooldownDs = 140, NeedsTarget = false, Trace = 2 },
        new RepairCombatProgram { Id = "patch", Role = "repair", Power = 30, CooldownTurns = 1, CooldownDs = 80, NeedsTarget = false, Trace = 1 },
        new TraceCombatProgram { Id = "trace", Role = "trace", Power = 6, CooldownTurns = 1, CooldownDs = 50, WindupDs = 30, ConsoleUsable = false, Biofeedback = true, TargetsBreach = true },
        new AttackCombatProgram { Id = "needle", Power = 12, ApCost = 1, CooldownDs = 10, Hostile = true, Biofeedback = true, Trace = 2 },
        new AttackCombatProgram { Id = "lance", Power = 34, CooldownTurns = 1, CooldownDs = 50, Hostile = true, Biofeedback = true, Trace = 6 },
        new ShieldCombatProgram { Id = "aegis", Role = "shield", Power = 70, ApCost = 3, CooldownTurns = 2, ShieldTurns = 2, CooldownDs = 160, NeedsTarget = false, Trace = 3 },
        new RepairCombatProgram { Id = "renew", Role = "repair", Power = 18, ApCost = 1, CooldownTurns = 1, CooldownDs = 100, NeedsTarget = false, Trace = 1 },
        new StunCombatProgram { Id = "jam", Role = "stun", Power = 20, ApCost = 1, StunTurns = 1, CooldownTurns = 2, CooldownDs = 120, Hostile = true, Trace = 4 }
    ]);
}
