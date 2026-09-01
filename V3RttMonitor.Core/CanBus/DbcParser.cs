using System.Globalization;
using System.Text.RegularExpressions;

namespace V3RttMonitor.Core.CanBus;

public static partial class DbcParser
{
    private const string NumberPattern = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?";

    [GeneratedRegex(@"^\s*BO_\s+(?<id>\d+)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<length>\d+)\s+(?<sender>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex MessageRegex();

    [GeneratedRegex(@"^\s*SG_\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?<mux>M|m\d+(?:M)?))?\s*:\s*(?<start>\d+)\|(?<length>\d+)@(?<order>[01])(?<sign>[+-])\s*\((?<factor>[^,]+),(?<offset>[^\)]+)\)\s*\[(?<minimum>[^|]+)\|(?<maximum>[^\]]+)\]\s*""(?<unit>[^""]*)""\s*(?<receiver>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SignalRegex();

    [GeneratedRegex(@"^\s*VAL_\s+(?<id>\d+)\s+(?<signal>[A-Za-z_][A-Za-z0-9_]*)\s+(?<values>.+?)\s*;\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValueTableRegex();

    [GeneratedRegex("(?<value>-?\\d+)\\s+\\\"(?<text>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePairRegex();

    [GeneratedRegex(@"^\s*CM_\s+BO_\s+(?<id>\d+)\s+""(?<comment>.*)""\s*;\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageCommentRegex();

    [GeneratedRegex(@"^\s*CM_\s+SG_\s+(?<id>\d+)\s+(?<signal>[A-Za-z_][A-Za-z0-9_]*)\s+""(?<comment>.*)""\s*;\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SignalCommentRegex();

    [GeneratedRegex(@"^\s*BA_\s+""GenMsgCycleTime""\s+BO_\s+(?<id>\d+)\s+(?<cycle>\d+)\s*;\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CycleTimeRegex();

    [GeneratedRegex(@"^\s*SIG_VALTYPE_\s+(?<id>\d+)\s+(?<signal>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<type>[12])\s*;\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SignalValueTypeRegex();

    public static async Task<DbcParseResult> ParseFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var result = Parse(text, Path.GetFileNameWithoutExtension(path));
        return result;
    }

    public static DbcParseResult Parse(string text, string? databaseName = null)
    {
        var database = new DbcDatabase { Name = SanitizeName(databaseName ?? "CAN_Database") };
        var diagnostics = new List<DbcDiagnostic>();
        DbcMessage? currentMessage = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;
            var messageMatch = MessageRegex().Match(line);
            if (messageMatch.Success)
            {
                if (!uint.TryParse(messageMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId)
                    || !int.TryParse(messageMatch.Groups["length"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
                {
                    diagnostics.Add(new(lineNumber, "报文ID或长度无效。", true));
                    currentMessage = null;
                    continue;
                }

                var isExtended = (fileId & 0x80000000u) != 0;
                currentMessage = new DbcMessage
                {
                    Id = isExtended ? fileId & 0x1FFFFFFFu : fileId,
                    IsExtended = isExtended,
                    Name = messageMatch.Groups["name"].Value,
                    Length = Math.Clamp(length, 0, 64),
                    Sender = messageMatch.Groups["sender"].Value,
                };
                database.Messages.Add(currentMessage);
                continue;
            }

            var signalMatch = SignalRegex().Match(line);
            if (signalMatch.Success)
            {
                if (currentMessage is null)
                {
                    diagnostics.Add(new(lineNumber, "SG_前没有BO_报文定义。", true));
                    continue;
                }

                if (!TryInteger(signalMatch, "start", out var start)
                    || !TryInteger(signalMatch, "length", out var length)
                    || !TryNumber(signalMatch, "factor", out var factor)
                    || !TryNumber(signalMatch, "offset", out var offset)
                    || !TryNumber(signalMatch, "minimum", out var minimum)
                    || !TryNumber(signalMatch, "maximum", out var maximum))
                {
                    diagnostics.Add(new(lineNumber, $"信号 {signalMatch.Groups["name"].Value} 的数值字段无效。", true));
                    continue;
                }

                var mux = signalMatch.Groups["mux"].Value;
                var signal = new DbcSignal
                {
                    Name = signalMatch.Groups["name"].Value,
                    StartBit = start,
                    Length = length,
                    ByteOrder = signalMatch.Groups["order"].Value == "1" ? DbcByteOrder.Intel : DbcByteOrder.Motorola,
                    IsSigned = signalMatch.Groups["sign"].Value == "-",
                    Factor = factor,
                    Offset = offset,
                    Minimum = minimum,
                    Maximum = maximum,
                    Unit = signalMatch.Groups["unit"].Value,
                    Receiver = signalMatch.Groups["receiver"].Value.Trim().TrimEnd(';'),
                    IsMultiplexer = mux == "M" || mux.EndsWith('M'),
                    MultiplexerValue = mux.StartsWith('m') && int.TryParse(mux.AsSpan(1).TrimEnd('M'), out var muxValue) ? muxValue : null,
                };
                currentMessage.Signals.Add(signal);
                var validation = DbcCodec.ValidateSignal(currentMessage, signal);
                foreach (var error in validation.Errors) diagnostics.Add(new(lineNumber, $"{signal.Name}: {error}", true));
                continue;
            }

            ApplyValueTable(database, line, lineNumber, diagnostics);
            ApplyComments(database, line);
            ApplyCycleTime(database, line);
            ApplySignalValueType(database, line);
        }

        foreach (var duplicate in database.Messages.GroupBy(message => message.Key).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new(0, $"报文ID {duplicate.Key} 重复定义。", true));
        }

        return new DbcParseResult { Database = database, Diagnostics = diagnostics };
    }

    private static void ApplyValueTable(DbcDatabase database, string line, int lineNumber, List<DbcDiagnostic> diagnostics)
    {
        var match = ValueTableRegex().Match(line);
        if (!match.Success || !uint.TryParse(match.Groups["id"].Value, out var fileId)) return;
        var message = FindByFileId(database, fileId);
        var signal = message?.Signals.FirstOrDefault(item => item.Name == match.Groups["signal"].Value);
        if (signal is null)
        {
            diagnostics.Add(new(lineNumber, "VAL_引用了不存在的报文或信号。"));
            return;
        }
        foreach (Match pair in ValuePairRegex().Matches(match.Groups["values"].Value))
        {
            if (long.TryParse(pair.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                signal.Choices[value] = pair.Groups["text"].Value.Replace("\\\"", "\"", StringComparison.Ordinal);
            }
        }
    }

    private static void ApplyComments(DbcDatabase database, string line)
    {
        var messageMatch = MessageCommentRegex().Match(line);
        if (messageMatch.Success && uint.TryParse(messageMatch.Groups["id"].Value, out var messageId))
        {
            var message = FindByFileId(database, messageId);
            if (message is not null) message.Comment = messageMatch.Groups["comment"].Value;
            return;
        }
        var signalMatch = SignalCommentRegex().Match(line);
        if (!signalMatch.Success || !uint.TryParse(signalMatch.Groups["id"].Value, out var signalMessageId)) return;
        var parent = FindByFileId(database, signalMessageId);
        var signal = parent?.Signals.FirstOrDefault(item => item.Name == signalMatch.Groups["signal"].Value);
        if (signal is not null) signal.Comment = signalMatch.Groups["comment"].Value;
    }

    private static void ApplyCycleTime(DbcDatabase database, string line)
    {
        var match = CycleTimeRegex().Match(line);
        if (!match.Success || !uint.TryParse(match.Groups["id"].Value, out var id) || !int.TryParse(match.Groups["cycle"].Value, out var cycle)) return;
        var message = FindByFileId(database, id);
        if (message is not null) message.CycleTimeMs = cycle;
    }

    private static void ApplySignalValueType(DbcDatabase database, string line)
    {
        var match = SignalValueTypeRegex().Match(line);
        if (!match.Success || !uint.TryParse(match.Groups["id"].Value, out var id)) return;
        var signal = FindByFileId(database, id)?.Signals.FirstOrDefault(item => item.Name == match.Groups["signal"].Value);
        if (signal is not null) signal.ValueType = match.Groups["type"].Value == "1" ? DbcSignalValueType.Float32 : DbcSignalValueType.Float64;
    }

    private static DbcMessage? FindByFileId(DbcDatabase database, uint fileId)
    {
        var extended = (fileId & 0x80000000u) != 0;
        var id = extended ? fileId & 0x1FFFFFFFu : fileId;
        return database.Messages.FirstOrDefault(message => message.Id == id && message.IsExtended == extended);
    }

    private static bool TryInteger(Match match, string group, out int value) => int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static bool TryNumber(Match match, string group, out double value) => double.TryParse(match.Groups[group].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static string SanitizeName(string value)
    {
        var sanitized = Regex.Replace(value, "[^A-Za-z0-9_]", "_");
        if (sanitized.Length == 0) return "Item";
        return char.IsDigit(sanitized[0]) ? "_" + sanitized : sanitized;
    }
}
