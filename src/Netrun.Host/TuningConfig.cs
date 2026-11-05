using System.Text.Json;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Entities.Combat;

namespace Netrun.Host;

/// <summary>
/// Необязательный JSON тюнинг-файл. Отсутствующие секции и поля сохраняют штатные значения,
/// поэтому пустой или частичный файл не меняет поведения симуляции.
/// </summary>
public sealed record NetrunTuningFile(CombatOptions? Combat = null, BotEngineOptions? Bots = null);

/// <summary>
/// Загружает игровой тюнинг CombatSimulation/BotEngine из NETRUN_TUNING_FILE и строит
/// соответствующие опции для RuntimeOptions; диапазоны проверяются сразу при старте.
/// </summary>
public static class TuningConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Читает путь из окружения; пустой путь или пустой файл → штатная конфигурация (null, null).</summary>
    public static (CombatOptions? Combat, BotEngineOptions? Bots) FromEnvironment(
        Func<string, string?>? read = null, Func<string, string>? readFile = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var path = read("NETRUN_TUNING_FILE");
        if (string.IsNullOrWhiteSpace(path)) return (null, null);
        readFile ??= File.ReadAllText;
        return Parse(readFile(path));
    }

    /// <summary>Разбирает и валидирует JSON; пустой текст сохраняет штатные значения, иначе бросает ArgumentException.</summary>
    public static (CombatOptions? Combat, BotEngineOptions? Bots) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        NetrunTuningFile? file;
        try
        {
            file = JsonSerializer.Deserialize<NetrunTuningFile>(json, Options);
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Invalid tuning file.", error);
        }
        if (file is null) throw new ArgumentException("Invalid tuning file.");
        // Строим реальные симуляции-пустышки, чтобы диапазоны отказали сразу при старте, а не при первом bind.
        _ = new CombatSimulation(options: file.Combat);
        if (file.Bots is not null) new BotEngine(file.Bots).Dispose();
        return (file.Combat, file.Bots);
    }
}
