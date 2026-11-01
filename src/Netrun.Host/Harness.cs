using System.Text;
using Netrun.Core;
using Netrun.Protocol;

namespace Netrun.Host;

/// <summary>Автономный стенд без сетевого endpoint и игровых эффектов; не заменяет live Host.</summary>
public static class Harness
{
    /// <summary>
    /// Читает ограниченные JSONL-записи и выводит результат каждого целого пакета.
    /// Первая запись задаёт epochs только для offline replay; ошибка завершает прогон с кодом 2.
    /// </summary>
    public static Task<int> ReplayAsync(Stream input, TextWriter output, TextWriter errors,
        CancellationToken cancellationToken = default) => ReplayCoreAsync(input, output, errors, false, cancellationToken);

    /// <summary>Автономный replay с обязательным snapshot/catch-up; EOF до Ready завершает прогон ошибкой.</summary>
    public static Task<int> ReplaySynchronizedAsync(Stream input, TextWriter output, TextWriter errors,
        CancellationToken cancellationToken = default) => ReplayCoreAsync(input, output, errors, true, cancellationToken);

    /// <summary>Общий ограниченный JSONL pipeline; формат режима задаётся до первого входа и не меняется по ошибке.</summary>
    private static async Task<int> ReplayCoreAsync(Stream input, TextWriter output, TextWriter errors,
        bool requireSnapshot, CancellationToken cancellationToken)
    {
        OfflineSession? session = null;
        try
        {
            await foreach (var body in ReadJsonLinesAsync(input, cancellationToken))
            {
                var batch = BatchJson.Decode(body);
                session ??= new OfflineSession(batch.Epochs, requireSnapshot);
                var result = session.Apply(batch);
                await output.WriteLineAsync(Encoding.UTF8.GetString(result.Body).AsMemory(), cancellationToken);
                if (result.Status is ApplyStatus.Rejected or ApplyStatus.ResyncRequired)
                    return 2;
            }
            if (session is null)
            {
                await errors.WriteLineAsync("empty_replay");
                return 2;
            }
            if (!session.IsComplete)
            {
                await errors.WriteLineAsync("snapshot_incomplete");
                return 2;
            }
            return 0;
        }
        catch (ProtocolException error)
        {
            await errors.WriteLineAsync(error.Code);
            return 2;
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>
    /// Проверяет бинарный framing на переданных потоках без владения ими.
    /// Epochs задаёт вызывающий стенд; это не handshake или авторизованное подключение DM.
    /// </summary>
    public static Task<int> FramedAsync(Stream input, Stream output, TextWriter errors,
        Epochs epochs, CancellationToken cancellationToken = default) =>
        FramedCoreAsync(input, output, errors, epochs, false, cancellationToken);

    /// <summary>Framing-стенд с обязательной синхронизацией; не открывает endpoint и не выполняет handshake.</summary>
    public static Task<int> FramedSynchronizedAsync(Stream input, Stream output, TextWriter errors,
        Epochs epochs, CancellationToken cancellationToken = default) =>
        FramedCoreAsync(input, output, errors, epochs, true, cancellationToken);

    /// <summary>Общий бинарный pipeline; неполный снимок нельзя принять как успешный disconnect.</summary>
    private static async Task<int> FramedCoreAsync(Stream input, Stream output, TextWriter errors,
        Epochs epochs, bool requireSnapshot, CancellationToken cancellationToken)
    {
        using var session = new OfflineSession(epochs, requireSnapshot);
        try
        {
            while (await FramedStream.ReadAsync(input, cancellationToken) is { } body)
            {
                var result = session.Apply(BatchJson.Decode(body));
                await FramedStream.WriteAsync(output, result.Body, cancellationToken);
                if (result.Status is ApplyStatus.Rejected or ApplyStatus.ResyncRequired)
                    return 2;
            }
            if (!session.IsComplete)
            {
                await errors.WriteLineAsync("snapshot_incomplete");
                return 2;
            }
            return 0;
        }
        catch (ProtocolException error)
        {
            await errors.WriteLineAsync(error.Code);
            return 2;
        }
    }

    /// <summary>Читает JSONL с ограничением строки до выделения неограниченной памяти; поддерживает LF и CRLF.</summary>
    private static async IAsyncEnumerable<byte[]> ReadJsonLinesAsync(Stream input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            var offset = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                Append(buffer, offset, i - offset);
                yield return TakeLine();
                offset = i + 1;
            }
            Append(buffer, offset, read - offset);
        }
        if (line.Length != 0)
            yield return TakeLine();

        // Добавляет фрагмент текущей строки только в пределах лимита кадра + CR.
        void Append(byte[] source, int offset, int count)
        {
            // Допускается один CR для CRLF; Decode отдельно проверит длину тела.
            if (line.Length + count > FramedStream.MaxFrameBytes + 1)
                throw new ProtocolException("frame_size");
            line.Write(source, offset, count);
        }

        // Передаёт вызывающему независимое тело; накопитель переиспользуется для следующей строки.
        byte[] TakeLine()
        {
            var length = checked((int)line.Length);
            var data = line.GetBuffer();
            if (length > 0 && data[length - 1] == (byte)'\r')
                length--;
            if (length == 0 || length > FramedStream.MaxFrameBytes)
                throw new ProtocolException("frame_size");
            var body = data.AsSpan(0, length).ToArray();
            line.SetLength(0);
            return body;
        }
    }
}
