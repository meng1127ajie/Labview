using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace V3RttMonitor.Core.CanBus;

public sealed class TcpCanFrameSource(string host, int port) : ICanFrameSource, ICanFrameSourceDiagnostics
{
    private const int MaximumPendingLineBytes = 64 * 1024;
    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private readonly Stopwatch _clock = new();
    private long _receivedBytes;
    private long _receivedLines;
    private long _parsedFrames;
    private long _parseErrors;
    private string _lastRawPreview = string.Empty;

    public string Name => $"TCP CAN {host}:{port}";
    public bool IsRunning => _readTask is { IsCompleted: false };
    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? StatusChanged;
    public CanFrameSourceStatistics GetStatistics() => new(
        Interlocked.Read(ref _receivedBytes),
        Interlocked.Read(ref _receivedLines),
        Interlocked.Read(ref _parsedFrames),
        Interlocked.Read(ref _parseErrors),
        _lastRawPreview);

    public string FormatStatistics()
    {
        var statistics = GetStatistics();
        var bytesText = statistics.ReceivedBytes >= 1024
            ? $"{statistics.ReceivedBytes / 1024.0:F1} KiB"
            : $"{statistics.ReceivedBytes} B";
        var text = $"原始 {bytesText} · {statistics.ReceivedLines:N0}行 · {statistics.ParsedFrames:N0}帧 · {statistics.ParseErrors:N0}失败";
        if (statistics.ReceivedBytes > 0 && statistics.ReceivedLines == 0)
        {
            text += " · 已收到数据但没有换行，可能不是文本CAN协议";
        }
        else if (statistics.ReceivedLines > 0 && statistics.ParsedFrames == 0)
        {
            text += " · 数据已到达但行格式未匹配";
        }
        return text;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _clock.Restart();
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _receivedLines, 0);
        Interlocked.Exchange(ref _parsedFrames, 0);
        Interlocked.Exchange(ref _parseErrors, 0);
        _lastRawPreview = string.Empty;
        StatusChanged?.Invoke($"已连接 {host}:{port}");
        _readTask = Task.Run(() => ReadLoopAsync(_client, _cts.Token), CancellationToken.None);
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var context = new CanTextParseContext();
        var readBuffer = new byte[8192];
        var pendingLine = new List<byte>(1024);
        try
        {
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                Interlocked.Add(ref _receivedBytes, count);
                _lastRawPreview = BuildPreview(readBuffer.AsSpan(0, count));
                for (var index = 0; index < count; index++)
                {
                    var value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        var line = Encoding.UTF8.GetString(pendingLine.ToArray()).TrimEnd('\r', '\0');
                        pendingLine.Clear();
                        if (line.Length == 0) continue;
                        Interlocked.Increment(ref _receivedLines);
                        context.FallbackTimestampSeconds = _clock.Elapsed.TotalSeconds;
                        if (CanTextFrameParser.TryParse(line, context, out var frame, out var error))
                        {
                            Interlocked.Increment(ref _parsedFrames);
                            FrameReceived?.Invoke(frame);
                        }
                        else
                        {
                            var failures = Interlocked.Increment(ref _parseErrors);
                            if (failures == 1 || failures % 100 == 0)
                            {
                                StatusChanged?.Invoke($"CAN文本解析失败 {failures:N0} 行：{error ?? "格式不支持"}；原始={line[..Math.Min(line.Length, 100)]}");
                            }
                        }
                    }
                    else if (pendingLine.Count < MaximumPendingLineBytes)
                    {
                        pendingLine.Add(value);
                    }
                }
            }
            if (!cancellationToken.IsCancellationRequested) StatusChanged?.Invoke("TCP CAN连接已关闭。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"TCP CAN异常：{exception.Message}");
        }
    }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        var client = Interlocked.Exchange(ref _client, null);
        client?.Close();
        var task = Interlocked.Exchange(ref _readTask, null);
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
        _clock.Stop();
        StatusChanged?.Invoke("TCP CAN已断开。");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static string BuildPreview(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes[..Math.Min(bytes.Length, 80)];
        var printable = sample.ToArray().All(value => value is (>= 0x20 and <= 0x7E) or (byte)'\r' or (byte)'\n' or (byte)'\t');
        return printable
            ? Encoding.UTF8.GetString(sample).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            : string.Join(' ', sample.ToArray().Select(value => value.ToString("X2")));
    }
}
