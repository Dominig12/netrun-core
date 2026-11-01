using System.Text;

namespace Netrun.Core;

/// <summary>Общие инварианты доменных фактов; используются mirror и каноническим checksum снимка.</summary>
internal static class NodeFactRules
{
    /// <summary>Отказ на непарные surrogate предотвращает неоднозначную идентичность при UTF-8 hashing.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Проверяет длину UTF-16, управляющие символы и корректность Unicode.</summary>
    internal static bool IsValidText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            return false;
        try { _ = StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    /// <summary>Проверяет metadata намерения и полное состояние; null Fact означает допустимый tombstone.</summary>
    internal static bool IsValidMutation(NodeMutation? mutation)
    {
        if (mutation is null || !IsValidText(mutation.NodeId, 128) || mutation.Generation == 0 || mutation.Revision == 0)
            return false;
        if (mutation.Fact is null)
            return true;
        var node = mutation.Fact;
        return node.NodeId == mutation.NodeId && node.Generation == mutation.Generation && node.Revision == mutation.Revision &&
            node.State is { Position: not null } state && IsValidText(state.Address, 256) &&
            IsValidText(state.NodeType, 256) && IsValidText(state.SectorId, 256) &&
            state.Position.X is >= 1 and <= 32767 && state.Position.Y is >= 1 and <= 32767 &&
            state.Position.Z is >= 1 and <= 4096;
    }
}
