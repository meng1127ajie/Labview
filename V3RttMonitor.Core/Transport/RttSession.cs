using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using V3RttMonitor.Core.Diagnostics;
using V3RttMonitor.Core.Protocol;

namespace V3RttMonitor.Core.Transport;

public enum RttConnectionState { Disconnected, Connecting, Connected, Reconnecting, Faulted }

public sealed record RttSessionStatistics(
    RttConnectionState State,
    long ReceivedFrames,
    long LostFrames,
    long GapEvents,
    long SequenceAnomalies,
    long SequenceStep,
    bool IsSequenceStepConfirmed,
    long TargetRestarts,
    long Reconnects,
    long ReceivedBytes,
    long ParserResynchronizations,
    long ParserDiscardedBytes,
    long LastSequence,
    double LastTargetTimeMs,
    int DetectedFloatCount,
    double RecentFramesPerSecond,
    double AverageFramesPerSecond,
    double RecentBytesPerSecond,
    double AverageBytesPerSecond,
    bool IsRecording);

public sealed class RttSession : IAsyncDisposable
{
    private readonly JustFloatParser _parser = new();
    private readonly SequenceContinuityTracker _sequenceTracker = new();
    private readonly JLinkServerHost _serverHost = new();
    private readonly object _statsGate = new();
    private readonly object _recordingGate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private TcpClient? _activeClient;
    private Stream? _recordingStream;
    private readonly Queue<long> _recentFrameTimestamps = [];
    private readonly Queue<(long Timestamp, int Count)> _recentByteSamples = [];
    private RttConnectionState _state = RttConnectionState.Disconnected;
    private long _reconnects;
    private long _receivedBytes;
    private double _lastTargetTimeMs;
    private long _firstFrameTimestamp;
    private long _firstByteTimestamp;
    private DateTime _lastDataReceivedTime = DateTime.MinValue;

    private static readonly long RateWindowTicks = checked((long)(2 * (double)Stopwatch.Frequency));

    public event Action<RttFrame>? FrameReceived;
    public event Action<RttConnectionState>? StateChanged;
    public event Action<string>? LogReceived;
    public event Action<bool>? DataActivityChanged;

    public bool IsRunning => _runTask is not null;
    public bool HasRecentDataActivity => (DateTime.Now - _lastDataReceivedTime).TotalSeconds < 2;

    public RttSession() => _serverHost.LogReceived += text => Log($"[J-Link] {text}");

    public Task StartAsync(RttSessionSettings settings, CancellationToken cancellationToken = default)
    {
        if (_runTask is not null) throw new InvalidOperationException("TCP会话已经在运行。");
        Validate(settings);
        ResetStatistics();
        _parser.SetFloatCount(settings.ExpectedFloatCount);
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunReconnectLoopAsync(settings, _runCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var task = _runTask;
        _runTask = null;
        if (task is null)
        {
            StopRecording();
            return;
        }
        _runCancellation?.Cancel();
        _activeClient?.Dispose();
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            _activeClient = null;
            StopRecording();
            await _serverHost.StopAsync().ConfigureAwait(false);
            SetState(RttConnectionState.Disconnected);
            DataActivityChanged?.Invoke(false);
        }
    }

    public void StartRecording(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
        lock (_recordingGate)
        {
            _recordingStream?.Dispose();
            _recordingStream = new BufferedStream(
                new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read), 64 * 1024);
        }
        Log($"开始记录原始TCP字节：{fullPath}");
    }

    public void StopRecording()
    {
        Stream? stream;
        lock (_recordingGate)
        {
            stream = _recordingStream;
            _recordingStream = null;
        }
        if (stream is null) return;
        stream.Flush();
        stream.Dispose();
        Log("原始数据记录已停止。");
    }

    public RttSessionStatistics GetStatistics()
    {
        lock (_statsGate)
        {
            var now = Stopwatch.GetTimestamp();
            PruneRateWindows(now);
            var continuity = _sequenceTracker.GetSnapshot();
            var averageFrameRate = CalculateLifetimeRate(continuity.ReceivedFrames, _firstFrameTimestamp, now);
            var recentFrameRate = CalculateRecentFrameRate(now);
            var averageByteRate = CalculateLifetimeRate(_receivedBytes, _firstByteTimestamp, now);
            var recentByteRate = CalculateRecentByteRate(now);
            lock (_recordingGate)
            {
                return new RttSessionStatistics(
                    _state, continuity.ReceivedFrames, continuity.LostFrames,
                    continuity.GapEvents, continuity.Anomalies,
                    continuity.NominalStep ?? 0, continuity.IsStepConfirmed,
                    continuity.Restarts, _reconnects,
                    _receivedBytes, _parser.Resynchronizations, _parser.DiscardedBytes,
                    continuity.LastSequence, _lastTargetTimeMs, _parser.FloatCount,
                    recentFrameRate, averageFrameRate,
                    recentByteRate, averageByteRate, _recordingStream is not null);
            }
        }
    }

    /// <summary>
    /// Starts a new statistics interval without interrupting the connection or
    /// touching the raw BIN recording stream.
    /// </summary>
    public void ClearStatistics() => ResetStatistics();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunReconnectLoopAsync(RttSessionSettings settings, CancellationToken cancellationToken)
    {
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(first ? RttConnectionState.Connecting : RttConnectionState.Reconnecting);
            if (!first) lock (_statsGate) _reconnects++;
            try
            {
                using var client = await ConnectClientAsync(settings, cancellationToken).ConfigureAwait(false);
                _activeClient = client;
                _parser.Reset();
                var stream = client.GetStream();
                var handshake = settings.Mode == TransportMode.JLinkRtt
                    ? "$$SEGGER_TELNET_ConfigStr=RTTCh;0$$"
                    : settings.HandshakeData;
                await SendHandshakeAsync(stream, handshake, cancellationToken).ConfigureAwait(false);
                SetState(RttConnectionState.Connected);
                Log($"TCP客户端已连接：{settings.Host}:{settings.Port}");
                await ReceiveAsync(stream, cancellationToken).ConfigureAwait(false);
                Log("TCP连接已关闭。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetState(RttConnectionState.Faulted);
                Log($"连接错误：{ex.Message}");
            }
            finally
            {
                _activeClient = null;
                DataActivityChanged?.Invoke(false);
            }
            first = false;
            await Task.Delay(settings.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TcpClient> ConnectClientAsync(RttSessionSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Mode == TransportMode.TcpDirect)
        {
            return await ConnectTcpAsync(settings.Host, settings.Port, settings.ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }

        var existing = await TryConnectTcpAsync(settings.Host, settings.Port, 500, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            Log("检测到已有RTT服务，直接附加。");
            return existing;
        }

        _serverHost.Start(settings);
        await WaitForPortAsync(settings.Host, settings.GdbPort, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        await TryContinueTargetAsync(settings, cancellationToken).ConfigureAwait(false);
        return await WaitForPortClientAsync(settings.Host, settings.Port, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TcpClient> ConnectTcpAsync(string host, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try { await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false); return client; }
        catch { client.Dispose(); throw; }
    }

    private static async Task<TcpClient?> TryConnectTcpAsync(string host, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        try { return await ConnectTcpAsync(host, port, timeoutMs, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }

    private static async Task WaitForPortAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var client = await TryConnectTcpAsync(host, port, 300, deadline.Token).ConfigureAwait(false);
            if (client is not null) return;
            await Task.Delay(200, deadline.Token).ConfigureAwait(false);
        }
    }

    private static async Task<TcpClient> WaitForPortClientAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var client = await TryConnectTcpAsync(host, port, 500, deadline.Token).ConfigureAwait(false);
            if (client is not null) return client;
            await Task.Delay(200, deadline.Token).ConfigureAwait(false);
        }
    }

    private async Task TryContinueTargetAsync(RttSessionSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ConnectTcpAsync(settings.Host, settings.GdbPort, 1500, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("$c#63"), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Log("已向GDB端口发送continue命令。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"目标continue未确认：{ex.Message}");
        }
    }

    private async Task ReceiveAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            lock (_statsGate)
            {
                var timestamp = Stopwatch.GetTimestamp();
                _receivedBytes += read;
                if (_firstByteTimestamp == 0) _firstByteTimestamp = timestamp;
                _recentByteSamples.Enqueue((timestamp, read));
                PruneRateWindows(timestamp);
            }
            lock (_recordingGate) _recordingStream?.Write(buffer, 0, read);
            _lastDataReceivedTime = DateTime.Now;
            DataActivityChanged?.Invoke(true);
            foreach (var frame in _parser.Feed(buffer.AsSpan(0, read))) ProcessFrame(frame);
        }
    }

    private void ProcessFrame(RttFrame frame)
    {
        lock (_statsGate)
        {
            var timestamp = Stopwatch.GetTimestamp();
            _sequenceTracker.Observe(frame.Sequence);
            _lastTargetTimeMs = frame.TimeMs;
            if (_firstFrameTimestamp == 0) _firstFrameTimestamp = timestamp;
            _recentFrameTimestamps.Enqueue(timestamp);
            PruneRateWindows(timestamp);
        }
        FrameReceived?.Invoke(frame);
    }

    private static async Task SendHandshakeAsync(NetworkStream stream, string handshake, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(handshake)) return;
        var expanded = handshake.Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\0", "\0", StringComparison.Ordinal);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(expanded), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ResetStatistics()
    {
        lock (_statsGate)
        {
            _sequenceTracker.Reset();
            _reconnects = _receivedBytes = 0;
            _lastTargetTimeMs = 0;
            _firstFrameTimestamp = 0;
            _firstByteTimestamp = 0;
            _recentFrameTimestamps.Clear();
            _recentByteSamples.Clear();
        }
    }

    private void PruneRateWindows(long now)
    {
        var cutoff = now - RateWindowTicks;
        while (_recentFrameTimestamps.TryPeek(out var timestamp) && timestamp < cutoff)
        {
            _recentFrameTimestamps.Dequeue();
        }
        while (_recentByteSamples.TryPeek(out var sample) && sample.Timestamp < cutoff)
        {
            _recentByteSamples.Dequeue();
        }
    }

    private double CalculateRecentFrameRate(long now)
    {
        if (_recentFrameTimestamps.Count == 0) return 0;
        var elapsed = Math.Min(2.0, Math.Max(.001, (now - _firstFrameTimestamp) / (double)Stopwatch.Frequency));
        return _recentFrameTimestamps.Count / elapsed;
    }

    private double CalculateRecentByteRate(long now)
    {
        if (_recentByteSamples.Count == 0) return 0;
        var bytes = _recentByteSamples.Sum(sample => (long)sample.Count);
        var elapsed = Math.Min(2.0, Math.Max(.001, (now - _firstByteTimestamp) / (double)Stopwatch.Frequency));
        return bytes / elapsed;
    }

    private static double CalculateLifetimeRate(long count, long firstTimestamp, long now)
    {
        if (count == 0 || firstTimestamp == 0) return 0;
        var elapsed = Math.Max(.001, (now - firstTimestamp) / (double)Stopwatch.Frequency);
        return count / elapsed;
    }

    private void SetState(RttConnectionState state)
    {
        lock (_statsGate) _state = state;
        StateChanged?.Invoke(state);
    }

    private void Log(string message) => LogReceived?.Invoke($"{DateTime.Now:HH:mm:ss.fff}  {message}");

    private static void Validate(RttSessionSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Host);
        if (settings.Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(settings.Port));
        if (settings.Mode == TransportMode.JLinkRtt && settings.SpeedKhz <= 0) throw new ArgumentOutOfRangeException(nameof(settings.SpeedKhz));
        if (settings.ExpectedFloatCount != 0
            && settings.ExpectedFloatCount is < JustFloatParser.MinimumFloatCount or > JustFloatParser.MaximumFloatCount)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.ExpectedFloatCount));
        }
    }
}
