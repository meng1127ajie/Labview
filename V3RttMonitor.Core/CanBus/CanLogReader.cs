namespace V3RttMonitor.Core.CanBus;

using Hsu.Formats.Blf;
using Hsu.Formats.Trace;

public interface ICanLogReader
{
    IReadOnlyCollection<string> SupportedExtensions { get; }
    Task<CanLogLoadResult> ReadAsync(string path, IProgress<CanLogLoadProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class TextCanLogReader : ICanLogReader
{
    private const int MaximumDiagnostics = 50;
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".asc", ".log", ".txt", ".csv"];

    public async Task<CanLogLoadResult> ReadAsync(string path, IProgress<CanLogLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException($"暂不支持{extension}日志。当前支持ASC、candump LOG、TXT和CSV文本帧。 ");
        var frames = new List<CanFrame>();
        var diagnostics = new List<string>();
        var context = new CanTextParseContext();
        long lines = 0;
        long skipped = 0;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lines++;
            var trimmed = line.Trim();
            if (trimmed.StartsWith("base dec", StringComparison.OrdinalIgnoreCase)) context.NumberBase = 10;
            else if (trimmed.StartsWith("base hex", StringComparison.OrdinalIgnoreCase)) context.NumberBase = 16;
            if (CanTextFrameParser.TryParse(line, context, out var frame, out var error)) frames.Add(frame);
            else if (error is not null)
            {
                skipped++;
                if (diagnostics.Count < MaximumDiagnostics) diagnostics.Add($"行{lines}: {error}");
            }
            if (lines % 10_000 == 0) progress?.Report(new(lines, frames.Count, skipped));
        }
        progress?.Report(new(lines, frames.Count, skipped));
        return new CanLogLoadResult { Path = path, Frames = frames, LinesRead = lines, SkippedLines = skipped, Diagnostics = diagnostics };
    }
}

public sealed class CanLogReaderRegistry
{
    private readonly List<ICanLogReader> _readers = [new BlfCanLogReader(), new TextCanLogReader()];
    public IReadOnlyList<ICanLogReader> Readers => _readers;

    public void Register(ICanLogReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _readers.Insert(0, reader);
    }

    public ICanLogReader Resolve(string path) => _readers.FirstOrDefault(reader => reader.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        ?? throw new NotSupportedException($"没有可读取{Path.GetExtension(path)}的CAN日志适配器。");
}

public sealed class BlfCanLogReader : ICanLogReader
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".blf"];

    public async Task<CanLogLoadResult> ReadAsync(string path, IProgress<CanLogLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var frames = new List<CanFrame>();
        var diagnostics = new List<string>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var session = new BlfParser().ParseFramesAsync(stream, cancellationToken);
        await foreach (var source in session.Frames.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var kind = source.IsRemote ? CanFrameKind.Remote : source is BlfFrame { IsError: true } ? CanFrameKind.Error : CanFrameKind.Data;
            frames.Add(new CanFrame
            {
                TimestampSeconds = source.Timestamp.TotalSeconds,
                Channel = Math.Max(1, source.Channel),
                Id = source.Id & 0x1FFFFFFFu,
                IsExtended = source.IsExtended,
                Direction = source.Direction switch
                {
                    CanFrameDirection.Rx => CanDirection.Rx,
                    CanFrameDirection.Tx or CanFrameDirection.TxRq => CanDirection.Tx,
                    _ => CanDirection.Unknown,
                },
                Kind = kind,
                IsFd = source.IsFd,
                Dlc = source.Dlc,
                Data = source.Data.ToArray(),
            });
            if (frames.Count % 10_000 == 0) progress?.Report(new(frames.Count, frames.Count, 0));
        }
        progress?.Report(new(frames.Count, frames.Count, 0));
        if (session.Model is BlfModel model && model.UnsupportedObjectTypes.Count > 0)
        {
            diagnostics.Add("BLF中包含未映射对象类型：" + string.Join(", ", model.UnsupportedObjectTypes.Select(type => $"0x{type:X}")));
        }
        return new CanLogLoadResult { Path = path, Frames = frames, LinesRead = frames.Count, SkippedLines = 0, Diagnostics = diagnostics };
    }
}
