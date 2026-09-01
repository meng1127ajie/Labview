namespace V3RttMonitor.Core.CanBus;

public enum CanDirection
{
    Unknown,
    Rx,
    Tx,
}

public enum CanFrameKind
{
    Data,
    Remote,
    Error,
}

public readonly record struct CanFrameKey(uint Id, bool IsExtended)
{
    public override string ToString() => IsExtended ? $"0x{Id:X8}x" : $"0x{Id:X3}";
}

public sealed record CanFrame
{
    public required double TimestampSeconds { get; init; }
    public int Channel { get; init; } = 1;
    public required uint Id { get; init; }
    public bool IsExtended { get; init; }
    public CanDirection Direction { get; init; } = CanDirection.Unknown;
    public CanFrameKind Kind { get; init; } = CanFrameKind.Data;
    public bool IsFd { get; init; }
    public bool BitrateSwitch { get; init; }
    public bool ErrorStateIndicator { get; init; }
    public int Dlc { get; init; }
    public byte[] Data { get; init; } = [];
    public int SegmentIndex { get; init; }
    public string SourceName { get; init; } = string.Empty;

    public CanFrameKey Key => new(Id, IsExtended);
    public string IdText => IsExtended ? $"{Id:X8}x" : $"{Id:X3}";
    public string DataText => Data.Length == 0 ? string.Empty : Convert.ToHexString(Data).Chunk(2).Select(chars => new string(chars)).Aggregate((left, right) => $"{left} {right}");
}

public enum CanLogMergeMode
{
    Replace,
    PreserveOriginalTime,
    AppendContinuous,
    AppendWithGap,
}

public sealed record CanLogSegment(string Name, IReadOnlyList<CanFrame> Frames);

public sealed record CanLogLoadProgress(long LinesRead, long FramesRead, long SkippedLines);

public sealed record CanLogLoadResult
{
    public required string Path { get; init; }
    public required IReadOnlyList<CanFrame> Frames { get; init; }
    public long LinesRead { get; init; }
    public long SkippedLines { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public double DurationSeconds => Frames.Count < 2 ? 0 : Math.Max(0, Frames[^1].TimestampSeconds - Frames[0].TimestampSeconds);
}
