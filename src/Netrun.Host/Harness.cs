using System.Globalization;
using System.Text;
using System.Text.Json;
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
    public static async Task<int> ReplayAsync(Stream input, TextWriter output, TextWriter errors,
        CancellationToken cancellationToken = default)
    {
        IWorldMirror? mirror = null;
        ulong outputSequence = 0;
        try
        {
            await foreach (var body in ReadJsonLinesAsync(input, cancellationToken))
            {
                var batch = BatchJson.Decode(body);
                mirror ??= new WorldMirror(batch.Epochs);
                var result = mirror.Apply(batch);
                await output.WriteLineAsync(Encoding.UTF8.GetString(EncodeResult(result, ++outputSequence)).AsMemory(), cancellationToken);
                if (result.Status is ApplyStatus.Rejected or ApplyStatus.ResyncRequired)
                    return 2;
            }
            if (mirror is null)
            {
                await errors.WriteLineAsync("empty_replay");
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

    /// <summary>
    /// Проверяет бинарный framing на переданных потоках без владения ими.
    /// Epochs задаёт вызывающий стенд; это не handshake или авторизованное подключение DM.
    /// </summary>
    public static async Task<int> FramedAsync(Stream input, Stream output, TextWriter errors,
        Epochs epochs, CancellationToken cancellationToken = default)
    {
        var mirror = new WorldMirror(epochs);
        ulong outputSequence = 0;
        try
        {
            while (await FramedStream.ReadAsync(input, cancellationToken) is { } body)
            {
                var result = mirror.Apply(BatchJson.Decode(body));
                await FramedStream.WriteAsync(output, EncodeResult(result, ++outputSequence), cancellationToken);
                if (result.Status is ApplyStatus.Rejected or ApplyStatus.ResyncRequired)
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

    /// <summary>Сериализует диагностику; все счётчики обмена остаются строками даже для больших ulong.</summary>
    private static byte[] EncodeResult(ApplyResult result, ulong sequence) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        kind = "apply_result",
        output_sequence = sequence.ToString(CultureInfo.InvariantCulture),
        status = result.Status switch
        {
            ApplyStatus.Applied => "applied",
            ApplyStatus.Duplicate => "duplicate",
            ApplyStatus.Rejected => "rejected",
            ApplyStatus.ResyncRequired => "resync_required",
            _ => throw new InvalidOperationException()
        },
        code = result.Code,
        applied_sequence = result.AppliedSequence.ToString(CultureInfo.InvariantCulture),
        simulation_time_ds = result.SimulationTimeDs,
        node_count = result.NodeCount,
        tracked_count = result.TrackedCount
    });

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
