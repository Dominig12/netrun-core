using System.Buffers.Binary;

namespace Netrun.Protocol;

/// <summary>
/// Читает и записывает кадры: четырёхбайтовая беззнаковая длина в network byte order и тело.
/// Не разбирает JSON и не владеет потоком; вызовы в одном направлении требуется сериализовать вызывающей стороне.
/// </summary>
public static class FramedStream
{
    /// <summary>
    /// Максимальный размер тела в байтах без четырёхбайтового заголовка: 1 MiB.
    /// Проверяется до выделения входного буфера и до начала записи выходного кадра.
    /// </summary>
    public const int MaxFrameBytes = 1_048_576;

    /// <summary>
    /// Собирает один кадр при любых границах чтения, оставляя следующие кадры в потоке.
    /// Возвращает новый массив тела, которым владеет вызывающая сторона; поток остаётся открытым.
    /// </summary>
    /// <param name="stream">Поток, чтение которого в данный момент принадлежит этому вызову.</param>
    /// <param name="cancellationToken">Отмена ожидания данных; частично прочитанный кадр не восстанавливается.</param>
    /// <returns>Тело кадра либо null, если EOF обнаружен до первого байта нового заголовка.</returns>
    /// <exception cref="ProtocolException">
    /// frame_size при нулевой или превышающей лимит длине; frame_truncated при EOF внутри кадра.
    /// </exception>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (first == 0)
            return null;
        await ReadRemainingAsync(stream, header.AsMemory(first), cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size is 0 or > MaxFrameBytes)
            throw new ProtocolException("frame_size");
        var body = new byte[(int)size];
        await ReadRemainingAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// Записывает заголовок и неизменяемое тело одного кадра, затем выполняет FlushAsync.
    /// Заимствует память тела до завершения вызова и оставляет поток открытым.
    /// </summary>
    /// <param name="stream">Поток с единственным последовательным писателем.</param>
    /// <param name="body">От 1 до MaxFrameBytes байт; JSON проверяет отдельный кодек.</param>
    /// <param name="cancellationToken">Отмена I/O; после частичной записи требуется восстановить канал обмена.</param>
    /// <exception cref="ProtocolException">frame_size; проверка выполняется до первой записи в поток.</exception>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        if (body.Length is 0 or > MaxFrameBytes)
            throw new ProtocolException("frame_size");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Заполняет весь переданный участок уже выделенного буфера, корректно обрабатывая короткие чтения.
    /// EOF здесь всегда означает frame_truncated: начало кадра уже принято вызывающим методом.
    /// </summary>
    private static async Task ReadRemainingAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new ProtocolException("frame_truncated");
            buffer = buffer[read..];
        }
    }
}
