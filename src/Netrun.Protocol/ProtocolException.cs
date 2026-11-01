namespace Netrun.Protocol;

/// <summary>
/// Ошибка формата обмена с диагностикой, не содержащей содержимое входного кадра.
/// Позволяет Host отклонять повреждённые данные без раскрытия идентификаторов и будущих payload.
/// </summary>
/// <param name="code">Стабильный код причины для журнала и машинной обработки.</param>
public sealed class ProtocolException(string code) : Exception(code)
{
    /// <summary>
    /// Безопасный код отказа; не включает JSON, пользовательские строки или исключение парсера.
    /// </summary>
    public string Code { get; } = code;
}
