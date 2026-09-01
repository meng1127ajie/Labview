using System.Globalization;
using System.Text;
using V3RttMonitor.Core.Protocol;

namespace V3RttMonitor.Core.Export;

public static class RttCsvExporter
{
    public static void Write(
        string path,
        IEnumerable<(int FrameIndex, RttFrame Frame)> rows,
        IReadOnlyList<RttFieldDescriptor> descriptors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(descriptors);

        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.Write("FrameIndex");
        foreach (var descriptor in descriptors)
        {
            writer.Write(',');
            writer.Write(descriptor.Key);
            if (!string.IsNullOrEmpty(descriptor.Unit))
            {
                writer.Write($"[{descriptor.Unit}]");
            }
        }
        writer.WriteLine();

        foreach (var (frameIndex, frame) in rows)
        {
            writer.Write(frameIndex.ToString(CultureInfo.InvariantCulture));
            foreach (var descriptor in descriptors)
            {
                writer.Write(',');
                writer.Write(frame.Values[descriptor.Index].ToString("R", CultureInfo.InvariantCulture));
            }
            writer.WriteLine();
        }
    }
}
