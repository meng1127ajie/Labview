namespace V3RttMonitor.Core.Hss;

public sealed record HssVariableSelection
{
    public required ElfSymbol Symbol { get; init; }
    public required ElfNumericType NumericType { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Symbol.Name : DisplayName;
    public uint Address => checked((uint)Symbol.Address);
    public int ByteCount => NumericType.GetByteCount();
}

public sealed record HssConfiguration
{
    public required string DllPath { get; init; }
    public string Device { get; init; } = "STM32G431RB";
    public int SpeedKhz { get; init; } = 4000;
    public int PeriodUs { get; init; } = 1000;
    public IReadOnlyList<HssVariableSelection> Variables { get; init; } = [];
}

public readonly record struct HssCapabilities(uint MaxBlocks, uint MaxFrequencyHz, uint RawCapabilities);

public sealed record HssSample(
    long Index,
    ulong TimestampUs,
    IReadOnlyList<double> Values,
    DateTimeOffset ReceivedAt);

public sealed record HssStatistics(
    bool IsRunning,
    long ReceivedSamples,
    double RecentSamplesPerSecond,
    double AverageSamplesPerSecond,
    long ReceivedBytes,
    HssCapabilities? Capabilities,
    string LastError);

public interface IHssSession : IAsyncDisposable
{
    event Action<HssSample>? SampleReceived;
    event Action<string>? LogReceived;
    event Action<bool>? StateChanged;

    bool IsRunning { get; }
    HssStatistics GetStatistics();
    void ClearStatistics();
    Task StartAsync(HssConfiguration configuration, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public static class JLinkHssCompatibility
{
    public static IReadOnlyList<string> ValidateExports(string dllPath) =>
        JLinkHssNativeApi.ValidateExports(dllPath);
}
