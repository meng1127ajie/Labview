using System.Globalization;
using System.Text;

namespace V3RttMonitor.Core.CanBus;

public static class DbcWriter
{
    public static async Task WriteFileAsync(string path, DbcDatabase database, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(path, Write(database), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    public static string Write(DbcDatabase database)
    {
        var builder = new StringBuilder();
        builder.AppendLine("VERSION \"JustFloat Studio\"");
        builder.AppendLine();
        builder.AppendLine("NS_ :");
        builder.AppendLine("    CM_");
        builder.AppendLine("    BA_DEF_");
        builder.AppendLine("    BA_");
        builder.AppendLine("    VAL_");
        builder.AppendLine("    SIG_VALTYPE_");
        builder.AppendLine();
        builder.AppendLine("BS_:");
        builder.AppendLine();
        builder.AppendLine("BU_: Vector__XXX");
        builder.AppendLine();

        foreach (var message in database.Messages.OrderBy(item => item.Id))
        {
            builder.Append("BO_ ").Append(message.FileId).Append(' ').Append(DbcParser.SanitizeName(message.Name)).Append(": ")
                .Append(Math.Clamp(message.Length, 0, 64)).Append(' ').AppendLine(string.IsNullOrWhiteSpace(message.Sender) ? "Vector__XXX" : DbcParser.SanitizeName(message.Sender));
            foreach (var signal in message.Signals)
            {
                var mux = signal.IsMultiplexer ? " M" : signal.MultiplexerValue is int value ? $" m{value}" : string.Empty;
                builder.Append(" SG_ ").Append(DbcParser.SanitizeName(signal.Name)).Append(mux).Append(" : ")
                    .Append(signal.StartBit).Append('|').Append(signal.Length).Append('@').Append((int)signal.ByteOrder).Append(signal.IsSigned ? '-' : '+')
                    .Append(" (").Append(Number(signal.Factor)).Append(',').Append(Number(signal.Offset)).Append(") [")
                    .Append(Number(signal.Minimum)).Append('|').Append(Number(signal.Maximum)).Append("] \"").Append(Escape(signal.Unit)).Append("\" ")
                    .AppendLine(string.IsNullOrWhiteSpace(signal.Receiver) ? "Vector__XXX" : string.Join(',', signal.Receiver.Split(',').Select(DbcParser.SanitizeName)));
            }
            builder.AppendLine();
        }

        foreach (var message in database.Messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Comment)) builder.Append("CM_ BO_ ").Append(message.FileId).Append(" \"").Append(Escape(message.Comment)).AppendLine("\";");
            if (message.CycleTimeMs is int cycle) builder.Append("BA_ \"GenMsgCycleTime\" BO_ ").Append(message.FileId).Append(' ').Append(cycle).AppendLine(";");
            foreach (var signal in message.Signals)
            {
                if (!string.IsNullOrWhiteSpace(signal.Comment)) builder.Append("CM_ SG_ ").Append(message.FileId).Append(' ').Append(DbcParser.SanitizeName(signal.Name)).Append(" \"").Append(Escape(signal.Comment)).AppendLine("\";");
                if (signal.Choices.Count > 0)
                {
                    builder.Append("VAL_ ").Append(message.FileId).Append(' ').Append(DbcParser.SanitizeName(signal.Name)).Append(' ');
                    foreach (var choice in signal.Choices.OrderBy(item => item.Key)) builder.Append(choice.Key).Append(" \"").Append(Escape(choice.Value)).Append("\" ");
                    builder.AppendLine(";");
                }
                if (signal.ValueType != DbcSignalValueType.Integer)
                {
                    builder.Append("SIG_VALTYPE_ ").Append(message.FileId).Append(' ').Append(DbcParser.SanitizeName(signal.Name)).Append(" : ").Append(signal.ValueType == DbcSignalValueType.Float32 ? 1 : 2).AppendLine(";");
                }
            }
        }
        return builder.ToString();
    }

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
