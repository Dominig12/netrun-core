namespace Netrun.Core.Entities.Bots;

/// <summary>Неизменяемая программа поведения; loadout описывает реальные файлы, но сам их не создаёт.</summary>
public sealed record BotProfile(string Id, string Name, string Planner, string Faction, double MaxIntegrity,
    int Storage, ValueList<string> Programs, ValueList<string> Daemons, int ThinkIdleDs = 40,
    int ThinkBusyDs = 20, int ThinkCombatDs = 12, double FleeRatio = 0.3,
    bool Sanctioned = false, int FileSize = 0, int LifetimeDs = 0, bool CarriesLanKey = false,
    string NamePrefix = "PROC", string Avatar = "orb", string Accent = "silver", string Tagline = "");

/// <summary>Точные profile параметры bot_programs/factions/police/recycling/agent_builder.dm.</summary>
public static class BotProfiles
{
    /// <summary>Все штатные архетипы без native type/proc строк и mutable объектов.</summary>
    public static ValueList<BotProfile> CreateDefaults()
    {
        var raider = Profile("raider", "Рейдер", "raider", "rogue", 120,
            ["assault", "strike", "barrier", "needle"]) with
        { FleeRatio = 0.2, CarriesLanKey = true, LifetimeDs = 7200, NamePrefix = "RAID", Avatar = "fox", Accent = "rose", Tagline = "ищет драку" };
        var scavenger = Profile("scavenger", "Сборщик обломков", "scavenge", "rogue", 100,
            ["strike", "barrier", "patch", "renew"]) with
        { FleeRatio = 0.5, CarriesLanKey = true, LifetimeDs = 6000, NamePrefix = "SCRAP", Avatar = "moth", Accent = "amber", Tagline = "чужих файлов не бывает" };
        var sentinel = Profile("sentinel", "Агент-страж", "guard", "owner", 120,
            ["strike", "blackout", "barrier", "lance"]) with
        { Storage = 32, FileSize = 4, NamePrefix = "GUARD", Avatar = "eye", Accent = "violet", Tagline = "охраняет узел" };
        var hacker = Profile("hacker", "Агент-взломщик", "hack", "owner", 90,
            ["blackout", "patch", "jam"]) with
        { Daemons = new(["autobreach", "cloak"]), FleeRatio = 0.5, FileSize = 5, NamePrefix = "HACK", Avatar = "cipher", Accent = "violet", Tagline = "подбирает доступ" };
        return new([
            raider,
            scavenger,
            Profile("custodian", "Санитар сети", "repair", "station", 100, ["strike", "patch", "needle"]) with
            { Sanctioned = true, ThinkIdleDs = 60, NamePrefix = "CLEAN", Accent = "mint", Tagline = "обслуживание сети" },
            sentinel,
            Profile("mender", "Агент-ремонтник", "mender", "owner", 80, ["patch", "renew"]) with
            { Storage = 32, FileSize = 4, NamePrefix = "MEND", Avatar = "ghost", Accent = "mint", Tagline = "чинит укрепления" },
            Profile("surveyor", "Агент-разведчик", "survey", "owner", 70, ["blackout", "patch", "jam"]) with
            { FleeRatio = 0.6, FileSize = 4, NamePrefix = "SURVEY", Avatar = "moth", Accent = "cyan", Tagline = "наблюдает и записывает" },
            Profile("collector", "Агент-сборщик", "collect", "owner", 80, ["barrier", "patch", "needle"]) with
            { Storage = 96, FleeRatio = 0.65, FileSize = 5, NamePrefix = "COLLECT", Accent = "amber", Tagline = "ничего не теряет" },
            sentinel with
            { Id = "interceptor", Name = "Агент-перехватчик", MaxIntegrity = 150, Storage = 48, FileSize = 5,
                Programs = new(["blackout", "strike", "fortify", "lance"]), NamePrefix = "INTERCEPT", Accent = "mint", Tagline = "перекрывает проход" },
            hacker,
            hacker with
            { Id = "breacher", Name = "Агент-пробойник", MaxIntegrity = 140, Storage = 80, FileSize = 6, FleeRatio = 0.2,
                Programs = new(["assault", "barrier", "patch", "aegis"]), Daemons = new(["autobreach", "icepick", "cloak"]),
                NamePrefix = "BREACH", Avatar = "fox", Accent = "rose", Tagline = "расчищает рубеж" },
            Profile("police", "Сетевая полиция", "police", "station", 160, ["assault", "strike", "blackout", "barrier", "patch"]) with
            { Sanctioned = true, ThinkIdleDs = 30, FleeRatio = 0.15, LifetimeDs = 4800, NamePrefix = "NETPOL", Avatar = "eye", Accent = "cyan", Tagline = "служит и защищает сеть" },
            Profile("recycler", "Станционный утилизатор", "recycle", "station", 90, ["barrier", "patch"]) with
            { Sanctioned = true, Storage = 128, ThinkIdleDs = 100, FleeRatio = 0.7, NamePrefix = "RECYCLE", Avatar = "moth", Accent = "mint", Tagline = "очищает сеть и сохраняет файлы" },
            raider with
            { Id = "redshift_raider", Name = "Налётчик Красного смещения", Faction = "redshift", MaxIntegrity = 100,
                LifetimeDs = 3600, Programs = new(["strike", "blackout", "barrier"]), NamePrefix = "RIFT" },
            hacker with
            { Id = "redshift_hacker", Name = "Диверсант Красного смещения", Faction = "redshift", FileSize = 0,
                LifetimeDs = 3000, NamePrefix = "WRAITH" },
            raider with
            { Id = "collective_guard", Name = "Страж Осколочного коллектива", Planner = "collective", Faction = "collective",
                LifetimeDs = 4200, Programs = new(["strike", "barrier", "patch"]), NamePrefix = "SHARD", Accent = "violet" },
            scavenger with
            { Id = "archive_collector", Name = "Собиратель Вольного архива", Faction = "archivists", LifetimeDs = 3600,
                Programs = new(["blackout", "patch"]), NamePrefix = "SCRIBE" },
            Profile("custom", "Собственный агент", "custom", "owner", 100, []) with
            { FileSize = 4, NamePrefix = "AGENT", Avatar = "cipher", Accent = "cyan" }
        ]);
    }

    /// <summary>Базовые значения разделяются по данным, не через mutable registry instance.</summary>
    private static BotProfile Profile(string id, string name, string planner, string faction, double integrity, string[] programs) =>
        new(id, name, planner, faction, integrity, 64, new(programs), new([]));
}
