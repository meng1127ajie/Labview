namespace V3RttMonitor.Core.CanBus;

public enum CanSourceTransport
{
    Hardware,
    Network,
    Virtual,
}

public enum CanLinkProtocol
{
    Classic,
    CanFd,
}

public sealed record CanEndpointDescriptor
{
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public required string Endpoint { get; init; }
    public CanSourceTransport Transport { get; init; } = CanSourceTransport.Hardware;
    public bool IsDetected { get; init; }
    public string Detail { get; init; } = string.Empty;

    public string DisplayText => IsDetected ? $"{DisplayName} · 已检测" : DisplayName;
    public override string ToString() => DisplayText;
}

public sealed record CanSourceConnectionOptions
{
    public required string Endpoint { get; init; }
    public CanLinkProtocol Protocol { get; init; } = CanLinkProtocol.Classic;
    public int NominalBitrate { get; init; } = 500_000;
    public int DataBitrate { get; init; } = 2_000_000;
    public bool ListenOnly { get; init; } = true;
    public bool InternalTermination { get; init; }
}

public interface ICanFrameSourceFactory
{
    string Id { get; }
    string DisplayName { get; }
    CanSourceTransport Transport { get; }
    Task<IReadOnlyList<CanEndpointDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default);
    ICanFrameSource Create(CanSourceConnectionOptions options);
}

public interface ICanFrameSource : IAsyncDisposable
{
    string Name { get; }
    bool IsRunning { get; }
    event Action<CanFrame>? FrameReceived;
    event Action<string>? StatusChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface ICanFrameSourceDiagnostics
{
    CanFrameSourceStatistics GetStatistics();
    string FormatStatistics();
}

public sealed record CanFrameSourceStatistics(
    long ReceivedBytes,
    long ReceivedLines,
    long ParsedFrames,
    long ParseErrors,
    string LastRawPreview,
    long DroppedFrames = 0,
    string AdapterState = "");
