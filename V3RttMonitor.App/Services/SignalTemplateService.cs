using ClosedXML.Excel;
using System.IO;

namespace V3RttMonitor.App.Services;

public sealed record SignalTemplateDefinition(
    int Index,
    string Key,
    string DisplayName,
    string Unit,
    string Group,
    bool Visible,
    int PlotGroup,
    string? Color,
    string Format,
    bool IntegerLike);

public static class SignalTemplateService
{
    private static readonly string[] RequiredHeaders =
    ["Index", "Key", "DisplayName", "Unit", "Group", "Visible", "PlotGroup", "Color", "Format", "IntegerLike"];

    public static IReadOnlyList<SignalTemplateDefinition> Load(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.TryGetWorksheet("Signals", out var named) ? named : workbook.Worksheet(1);
        var header = sheet.Row(1).CellsUsed().ToDictionary(
            cell => cell.GetString().Trim(), cell => cell.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);
        var missing = RequiredHeaders.Where(name => !header.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Excel缺少列：{string.Join(", ", missing)}");
        }

        var result = new List<SignalTemplateDefinition>();
        var indexes = new HashSet<int>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            var indexText = Text(row, "Index");
            if (string.IsNullOrWhiteSpace(indexText)) continue;
            if (!int.TryParse(indexText, out var index) || index is < 0 or >= 1024)
            {
                throw new InvalidDataException($"第{row}行Index无效：{indexText}");
            }
            if (!indexes.Add(index)) throw new InvalidDataException($"Index {index} 重复。");

            var key = Text(row, "Key");
            var displayName = Text(row, "DisplayName");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(displayName))
            {
                throw new InvalidDataException($"第{row}行Key或DisplayName为空。");
            }
            var plotGroup = ParseInt(Text(row, "PlotGroup"), 1);
            if (plotGroup is < 1 or > 8) throw new InvalidDataException($"第{row}行PlotGroup应为1~8。");
            var color = Text(row, "Color");
            if (!string.IsNullOrWhiteSpace(color) && !IsHexColor(color))
            {
                throw new InvalidDataException($"第{row}行Color应为#RRGGBB。");
            }

            result.Add(new SignalTemplateDefinition(
                index,
                key,
                displayName,
                Text(row, "Unit"),
                string.IsNullOrWhiteSpace(Text(row, "Group")) ? "扩展" : Text(row, "Group"),
                ParseBool(Text(row, "Visible")),
                plotGroup,
                string.IsNullOrWhiteSpace(color) ? null : color,
                string.IsNullOrWhiteSpace(Text(row, "Format")) ? "G7" : Text(row, "Format"),
                ParseBool(Text(row, "IntegerLike"))));
        }
        if (result.Count == 0) throw new InvalidDataException("Excel中没有信号配置行。");
        return result.OrderBy(item => item.Index).ToArray();

        string Text(int row, string column) => sheet.Cell(row, header[column]).GetFormattedString().Trim();
    }

    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
    private static bool ParseBool(string text) => text.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "是" or "显示";
    private static bool IsHexColor(string text) => text.Length == 7 && text[0] == '#' && text[1..].All(Uri.IsHexDigit);
}
