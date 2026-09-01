namespace V3RttMonitor.Core.Transport;

public enum TransportMode { JLinkRtt, TcpDirect }

public sealed record RttSessionSettings
{
    public TransportMode Mode { get; init; } = TransportMode.TcpDirect;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 19021;
    public string HandshakeData { get; init; } = "plot0";
    public int ExpectedFloatCount { get; init; }
    public int ReconnectIntervalMs { get; init; } = 1000;
    public int ConnectTimeoutMs { get; init; } = 5000;
    public string JLinkDirectory { get; init; } = @"C:\Program Files\SEGGER\JLink_V952";
    public string Device { get; init; } = "STM32G431RB";
    public string Interface { get; init; } = "SWD";
    public int SpeedKhz { get; init; } = 4000;
    public int GdbPort { get; init; } = 2331;
}
