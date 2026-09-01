using System.Diagnostics;

namespace V3RttMonitor.Core.Hss;

public sealed class JLinkHssSession : IHssSession
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _readTask;
    private JLinkHssNativeApi? _api;
    private long _receivedSamples;
    private long _receivedBytes;
    private long _firstSampleTick;
    private long _lastSampleTick;
    private readonly Queue<long> _recentTicks = new();
    private HssCapabilities? _capabilities;
    private string _lastError = string.Empty;

    public event Action<HssSample>? SampleReceived;
    public event Action<string>? LogReceived;
    public event Action<bool>? StateChanged;
    public bool IsRunning => _readTask is { IsCompleted: false };

    public async Task StartAsync(HssConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("HSS已经运行。");
        Validate(configuration);
        var api = new JLinkHssNativeApi(configuration.DllPath);
        try
        {
            Log("正在打开J-Link HSS连接…");
            api.OpenAndConnect(configuration.Device, configuration.SpeedKhz);
            var caps = api.GetCapabilities();
            if (caps.MaxBlocks > 0 && configuration.Variables.Count > caps.MaxBlocks)
            {
                throw new InvalidOperationException($"选择了{configuration.Variables.Count}个变量，探针最多支持{caps.MaxBlocks}个。 ");
            }
            var requestedHz = 1_000_000.0 / configuration.PeriodUs;
            if (caps.MaxFrequencyHz > 0 && requestedHz > caps.MaxFrequencyHz)
            {
                throw new InvalidOperationException($"请求{requestedHz:F0}Hz，探针能力上限{caps.MaxFrequencyHz}Hz。");
            }
            api.Start(configuration.Variables, configuration.PeriodUs);

            lock (_gate)
            {
                _receivedSamples = _receivedBytes = 0;
                _firstSampleTick = _lastSampleTick = 0;
                _recentTicks.Clear();
                _lastError = string.Empty;
                _capabilities = caps;
            }
            _api = api;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readTask = ReadLoopAsync(configuration, api, _cancellation.Token);
            StateChanged?.Invoke(true);
            Log($"HSS已启动：{configuration.Variables.Count}变量，周期{configuration.PeriodUs}us。");
            await Task.CompletedTask;
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync(HssConfiguration configuration, JLinkHssNativeApi api, CancellationToken cancellationToken)
    {
        var decoder = new HssSampleDecoder(configuration.Variables);
        var stride = decoder.SampleSize;
        var bufferSize = Math.Max(stride, (64 * 1024 / stride) * stride);
        var buffer = new byte[bufferSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = api.Read(buffer);
                if (count == 0)
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                foreach (var sample in decoder.Decode(buffer.AsSpan(0, count)))
                {
                    lock (_gate)
                    {
                        _receivedSamples++;
                        _receivedBytes += stride;
                        var now = Stopwatch.GetTimestamp();
                        if (_firstSampleTick == 0) _firstSampleTick = now;
                        _lastSampleTick = now;
                        _recentTicks.Enqueue(now);
                        Prune(now);
                    }
                    SampleReceived?.Invoke(sample);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate) _lastError = exception.Message;
            Log($"HSS读取异常：{exception.Message}");
        }
        finally { StateChanged?.Invoke(false); }
    }

    public async Task StopAsync()
    {
        var task = _readTask;
        _readTask = null;
        _cancellation?.Cancel();
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        var api = _api;
        _api = null;
        if (api is not null)
        {
            api.Stop();
            api.Dispose();
        }
        StateChanged?.Invoke(false);
        Log("HSS已停止并释放J-Link连接。");
    }

    public HssStatistics GetStatistics()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            Prune(now);
            var rate = 0.0;
            if (_recentTicks.Count >= 2)
            {
                var seconds = (_recentTicks.Last() - _recentTicks.Peek()) / (double)Stopwatch.Frequency;
                if (seconds > 0) rate = (_recentTicks.Count - 1) / seconds;
            }
            var average = _receivedSamples < 2 || _lastSampleTick <= _firstSampleTick
                ? 0
                : (_receivedSamples - 1) * Stopwatch.Frequency / (double)(_lastSampleTick - _firstSampleTick);
            return new HssStatistics(IsRunning, _receivedSamples, rate, average, _receivedBytes, _capabilities, _lastError);
        }
    }

    public void ClearStatistics()
    {
        lock (_gate)
        {
            _receivedSamples = 0;
            _receivedBytes = 0;
            _firstSampleTick = _lastSampleTick = 0;
            _recentTicks.Clear();
            _lastError = string.Empty;
        }
    }

    private void Prune(long now)
    {
        var cutoff = now - 2 * Stopwatch.Frequency;
        while (_recentTicks.Count > 0 && _recentTicks.Peek() < cutoff) _recentTicks.Dequeue();
    }

    private static void Validate(HssConfiguration configuration)
    {
        if (configuration.Variables.Count == 0) throw new InvalidOperationException("至少选择一个HSS变量。");
        if (configuration.PeriodUs <= 0) throw new ArgumentOutOfRangeException(nameof(configuration.PeriodUs));
        if (configuration.SpeedKhz <= 0) throw new ArgumentOutOfRangeException(nameof(configuration.SpeedKhz));
        foreach (var variable in configuration.Variables)
        {
            if (!variable.Symbol.IsHssAddressSupported) throw new InvalidOperationException($"变量地址超出32位：{variable.Name}");
            if (variable.ByteCount <= 0) throw new InvalidOperationException($"变量类型未设置：{variable.Name}");
            if ((ulong)variable.ByteCount != variable.Symbol.Size)
            {
                throw new InvalidOperationException($"变量{variable.Name}类型大小{variable.ByteCount}与ELF大小{variable.Symbol.Size}不一致。");
            }
        }
    }

    private void Log(string text) => LogReceived?.Invoke($"{DateTime.Now:HH:mm:ss.fff}  {text}");
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
