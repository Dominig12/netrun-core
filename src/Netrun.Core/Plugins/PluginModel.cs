namespace Netrun.Core.Plugins;

/// <summary>Версия публичного контракта расширений, независимая от версии конкретного плагина.</summary>
public static class NetrunPluginApi
{
    /// <summary>Поддерживаемая версия composition API.</summary>
    public const int Version = 1;
}

/// <summary>
/// Неизменяемое описание плагина: стабильный идентификатор, отображаемое имя и версия.
/// Метаданные не влияют на вычисления ядра и используются только для диагностики и журналирования.
/// </summary>
/// <param name="Id">Стабильный токен (строчные латинские буквы, цифры, точка, дефис, подчёркивание; 1..64 символа); уникален в наборе.</param>
/// <param name="DisplayName">Человекочитаемое имя для мониторинга; непустое, до 128 символов.</param>
/// <param name="Version">Версия плагина в произвольной форме; непустая, до 32 символов.</param>
public sealed record PluginMetadata(string Id, string DisplayName, string Version);

/// <summary>Ошибка состава или конфигурации плагинов с безопасным стабильным кодом, не содержащим входных данных.</summary>
/// <param name="code">Стабильный код причины для журнала, диагностики и воспроизводимых тестов.</param>
public sealed class PluginException(string code) : Exception(code)
{
    /// <summary>Код отказа composition, пригодный для журнала и ответа вызывающему коду.</summary>
    public string Code { get; } = code;
}
