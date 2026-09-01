using System.Diagnostics;

namespace V3RttMonitor.Core.Hss;

public sealed class SimulatedHssSession : IHssSession
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private long _samples;
    private long _bytes;
    private long _firstTick;
    private long _lastTick;
    private int _periodUs;

    public event Action<HssSample>? SampleReceived;
    public event Action<string>? LogReceived;
    public event Action<bool>? StateChanged;
    public bool IsRunning => _task is { IsCompleted: false };

    public Task StartAsync(HssConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("HSS模拟已经运行。");
        if (configuration.Variables.Count == 0) throw new InvalidOperationException("至少选择一个HSS变量。");
        _periodUs = configuration.PeriodUs;
        ClearStatistics();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = RunAsync(configuration, _cancellation.Token);
        StateChanged?.Invoke(true);
        LogReceived?.Invoke($"{DateTime.Now:HH:mm:ss.fff}  HSS离线模拟已启动（不访问J-Link）。");
        return Task.CompletedTask;
    }

    private async Task RunAsync(HssConfiguration configuration, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var index = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsedUs = clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                var dueSamples = elapsedUs / configuration.PeriodUs + 1;
                while (index < dueSamples)
                {
                    var values = new double[configuration.Variables.Count];
                    for (var i = 0; i < values.Length; i++)
                    {
                        var phase = index * configuration.PeriodUs / 1_000_000.0 * (i + 1) * Math.PI * 2;
                        values[i] = configuration.Variables[i].NumericType is ElfNumericType.Float32 or ElfNumericType.Float64
                            ? Math.Sin(phase) * (i + 1)
                            : (index + i * 10) % 100;
                    }
                    var now = Stopwatch.GetTimestamp();
                    lock (_gate)
                    {
                        if (_firstTick == 0) _firstTick = now;
                        _lastTick = now;
                        _samples++;
                        _bytes += sizeof(uint) + configuration.Variables.Sum(item => item.ByteCount);
                    }
                    var timestamp = checked((ulong)index * (ulong)configuration.PeriodUs);
                    SampleReceived?.Invoke(new HssSample(index++, timestamp, values, DateTimeOffset.UtcNow));
                }
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { StateChanged?.Invoke(false); }
    }

    public async Task StopAsync()
    {
        _cancellation?.Cancel();
        if (_task is not null)
        {
            try { await _task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _task = null;
        _cancellation?.Dispose();
        _cancellation = null;
        StateChanged?.Invoke(false);
    }

    public HssStatistics GetStatistics()
    {
        lock (_gate)
        {
            var rate = _samples < 2 || _lastTick <= _firstTick
                ? 0
                : (_samples - 1) * Stopwatch.Frequency / (double)(_lastTick - _firstTick);
            HssCapabilities? capabilities = _periodUs > 0
                ? new HssCapabilities(100, checked((uint)Math.Max(1, 1_000_000 / _periodUs)), 0)
                : null;
            return new HssStatistics(IsRunning, _samples, rate, rate, _bytes, capabilities, string.Empty);
        }
    }

    public void ClearStatistics()
    {
        lock (_gate)
        {
            _samples = _bytes = 0;
            _firstTick = _lastTick = 0;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
