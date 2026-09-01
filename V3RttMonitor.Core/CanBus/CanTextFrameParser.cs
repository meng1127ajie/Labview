using System.Globalization;
using System.Text.RegularExpressions;

namespace V3RttMonitor.Core.CanBus;

public sealed class CanTextParseContext
{
    public int NumberBase { get; set; } = 16;
    public double FallbackTimestampSeconds { get; set; }
}

public static partial class CanTextFrameParser
{
    [GeneratedRegex(@"^\s*\((?<time>\d+(?:\.\d+)?)\)\s+(?<channel>\S+)\s+(?<id>[0-9A-Fa-f]+)(?<separator>##|#)(?<payload>[0-9A-Fa-f]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CandumpRegex();

    [GeneratedRegex(@"^\s*(?<id>[0-9A-Fa-f]+)(?<extended>x)?(?<separator>##|#)(?<payload>[0-9A-Fa-f]*)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CompactRegex();

    public static bool TryParse(string line, CanTextParseContext context, out CanFrame frame, out string? error)
    {
        frame = null!;
        error = null;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith(';')) return false;

        var candump = CandumpRegex().Match(trimmed);
        if (candump.Success) return TryParseCandump(candump, context, out frame, out error);
        var compact = CompactRegex().Match(trimmed);
        if (compact.Success) return TryParseCompact(compact, context, out frame, out error);

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2 && double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp))
        {
            if (tokens[1].Equals("CANFD", StringComparison.OrdinalIgnoreCase)) return TryParseVectorFd(tokens, timestamp, context, out frame, out error);
            if (tokens.Length >= 6 && int.TryParse(tokens[1], out var channel)) return TryParseVectorClassic(tokens, timestamp, channel, context, out frame, out error);
        }

        if (trimmed.Contains(',')) return TryParseCsv(trimmed, context, out frame, out error);
        return false;
    }

    private static bool TryParseCandump(Match match, CanTextParseContext context, out CanFrame frame, out string? error)
    {
        frame = null!;
        error = null;
        if (!double.TryParse(match.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp)
            || !uint.TryParse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) return false;
        var channel = TrailingNumber(match.Groups["channel"].Value);
        var payload = match.Groups["payload"].Value;
        var fd = match.Groups["separator"].Value == "##";
        var flags = 0;
        if (fd && payload.Length > 0 && int.TryParse(payload.AsSpan(0, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags)) payload = payload[1..];
        if (!TryHexBytes(payload, out var data, out error)) return false;
        frame = NewFrame(timestamp, channel, id, id > 0x7FF, CanDirection.Rx, data, fd, (flags & 1) != 0, (flags & 2) != 0);
        return true;
    }

    private static bool TryParseCompact(Match match, CanTextParseContext context, out CanFrame frame, out string? error)
    {
        frame = null!;
        error = null;
        if (!uint.TryParse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) return false;
        var fd = match.Groups["separator"].Value == "##";
        var payload = match.Groups["payload"].Value;
        var flags = 0;
        if (fd && payload.Length > 0 && int.TryParse(payload.AsSpan(0, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags)) payload = payload[1..];
        if (!TryHexBytes(payload, out var data, out error)) return false;
        frame = NewFrame(context.FallbackTimestampSeconds, 1, id, match.Groups["extended"].Success || id > 0x7FF, CanDirection.Rx, data, fd, (flags & 1) != 0, (flags & 2) != 0);
        return true;
    }

    private static bool TryParseVectorClassic(string[] tokens, double timestamp, int channel, CanTextParseContext context, out CanFrame frame, out string? error)
    {
        frame = null!;
        error = null;
        var idText = tokens[2];
        var extended = idText.EndsWith("x", StringComparison.OrdinalIgnoreCase);
        if (extended) idText = idText[..^1];
        if (!TryUnsigned(idText, context.NumberBase, out var id)) return false;
        var direction = ParseDirection(tokens[3]);
        var type = tokens[4];
        if (type.Equals("r", StringComparison.OrdinalIgnoreCase))
        {
            var remoteDlc = tokens.Length > 5 && TryUnsigned(tokens[5], context.NumberBase, out var rawDlc) ? (int)rawDlc : 0;
            frame = NewFrame(timestamp, channel, id, extended || id > 0x7FF, direction, [], false, false, false, CanFrameKind.Remote, remoteDlc);
            return true;
        }
        if (!type.Equals("d", StringComparison.OrdinalIgnoreCase) || tokens.Length < 6 || !TryUnsigned(tokens[5], context.NumberBase, out var dlc)) return false;
        var count = Math.Min((int)dlc, Math.Min(8, tokens.Length - 6));
        var data = new byte[count];
        for (var index = 0; index < count; index++)
        {
            if (!TryUnsigned(tokens[index + 6], context.NumberBase, out var value) || value > byte.MaxValue)
            {
                error = $"无效数据字节：{tokens[index + 6]}";
                return false;
            }
            data[index] = (byte)value;
        }
        frame = NewFrame(timestamp, channel, id, extended || id > 0x7FF, direction, data, false, false, false, CanFrameKind.Data, (int)dlc);
        return true;
    }

    private static bool TryParseVectorFd(string[] tokens, double timestamp, CanTextParseContext context, out CanFrame frame, out string? error)
    {
        frame = null!;
        error = null;
        if (tokens.Length < 9 || !int.TryParse(tokens[2], out var channel)) return false;
        var direction = ParseDirection(tokens[3]);
        var idText = tokens[4];
        var extended = idText.EndsWith("x", StringComparison.OrdinalIgnoreCase);
        if (extended) idText = idText[..^1];
        if (!TryUnsigned(idText, context.NumberBase, out var id)) return false;

        for (var field = 5; field + 3 < tokens.Length; field++)
        {
            if (tokens[field] is not ("0" or "1") || tokens[field + 1] is not ("0" or "1")) continue;
            if (!TryUnsigned(tokens[field + 2], context.NumberBase, out var dlc) || !int.TryParse(tokens[field + 3], out var dataLength)) continue;
            if (dataLength < 0 || dataLength > 64 || field + 4 + dataLength > tokens.Length) continue;
            var data = new byte[dataLength];
            var valid = true;
            for (var index = 0; index < dataLength; index++)
            {
                if (!TryUnsigned(tokens[field + 4 + index], context.NumberBase, out var value) || value > byte.MaxValue) { valid = false; break; }
                data[index] = (byte)value;
            }
            if (!valid) continue;
            frame = NewFrame(timestamp, channel, id, extended || id > 0x7FF, direction, data, true, tokens[field] == "1", tokens[field + 1] == "1", CanFrameKind.Data, (int)dlc);
            return true;
        }
        error = "无法识别CAN FD字段布局。";
        return false;
    }

    private static bool TryParseCsv(string line, CanTextParseContext context, out CanFrame frame, out string? error)
    {
        frame = null!;
        error = null;
        var fields = line.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length < 5 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp) || !int.TryParse(fields[1], out var channel)) return false;
        var idText = fields[2].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        var extended = idText.EndsWith("x", StringComparison.OrdinalIgnoreCase);
        if (extended) idText = idText[..^1];
        if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) return false;
        var joined = string.Concat(fields.Skip(4)).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (!TryHexBytes(joined, out var data, out error)) return false;
        frame = NewFrame(timestamp, channel, id, extended || id > 0x7FF, ParseDirection(fields[3]), data, data.Length > 8, false, false);
        return true;
    }

    private static CanFrame NewFrame(double timestamp, int channel, uint id, bool extended, CanDirection direction, byte[] data, bool fd, bool brs, bool esi, CanFrameKind kind = CanFrameKind.Data, int? dlc = null) => new()
    {
        TimestampSeconds = timestamp,
        Channel = Math.Max(1, channel),
        Id = id & 0x1FFFFFFFu,
        IsExtended = extended,
        Direction = direction,
        Data = data,
        IsFd = fd,
        BitrateSwitch = brs,
        ErrorStateIndicator = esi,
        Kind = kind,
        Dlc = dlc ?? data.Length,
    };

    private static bool TryHexBytes(string text, out byte[] bytes, out string? error)
    {
        error = null;
        if (text.Length % 2 != 0) { bytes = []; error = "十六进制数据长度必须为偶数。"; return false; }
        bytes = new byte[text.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!byte.TryParse(text.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
            {
                bytes = [];
                error = "十六进制数据包含无效字符。";
                return false;
            }
        }
        return true;
    }

    private static bool TryUnsigned(string text, int numberBase, out uint value)
    {
        return uint.TryParse(text, numberBase == 10 ? NumberStyles.Integer : NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static CanDirection ParseDirection(string value) => value.Equals("Rx", StringComparison.OrdinalIgnoreCase) ? CanDirection.Rx : value.Equals("Tx", StringComparison.OrdinalIgnoreCase) ? CanDirection.Tx : CanDirection.Unknown;
    private static int TrailingNumber(string value)
    {
        var digits = new string(value.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var number) ? Math.Max(1, number + (value.StartsWith("can", StringComparison.OrdinalIgnoreCase) ? 1 : 0)) : 1;
    }
}
