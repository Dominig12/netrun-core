namespace Netrun.Core.Transport;

/// <summary>
/// Сохраняет один непринятый head на producer, пока pipelined хвост получает WouldBlock.
/// Нулевой watermark резервирует identity без расходования положительного request serial;
/// общая память ограничена тем же MaxProducers, что и обычная replay-защита binding.
/// </summary>
internal sealed class ProducerAdmissionFence(Dictionary<string, ulong> watermarks, int maximumProducers)
{
    /// <summary>Только ID/serial: payload, facts и ссылки на native-объекты здесь не удерживаются.</summary>
    private readonly Dictionary<string, ulong> _heads = new(StringComparer.Ordinal);

    /// <summary>Новая команда не обгоняет ранее непринятый запрос этого producer.</summary>
    internal bool Blocks(RequestKey key) => _heads.TryGetValue(key.ProducerId, out var head) && head != key.Sequence;

    /// <summary>WouldBlock сохраняет head; terminal admission освобождает его для последующих serials.</summary>
    internal TransportAdmission Complete(TransportAdmission admission)
    {
        var key = admission.Key;
        if (!TransportFingerprint.ValidKey(key)) return admission;
        if (admission.Status != AdmissionStatus.WouldBlock)
        {
            Release(key);
            return admission;
        }
        if (admission.Code is "producer_backpressure" or "producer_capacity") return admission;
        if (!watermarks.ContainsKey(key.ProducerId) && watermarks.Count < maximumProducers)
            watermarks.Add(key.ProducerId, 0);
        if (watermarks.ContainsKey(key.ProducerId)) _heads.TryAdd(key.ProducerId, key.Sequence);
        return admission;
    }

    /// <summary>Явная отмена забытого native consumer снимает fence даже до domain admission.</summary>
    internal void Release(RequestKey key)
    {
        if (_heads.TryGetValue(key.ProducerId, out var head) && head == key.Sequence)
            _heads.Remove(key.ProducerId);
    }

    /// <summary>Disconnect/Dispose прекращает все прежние admission ожидания.</summary>
    internal void Clear() => _heads.Clear();
}
