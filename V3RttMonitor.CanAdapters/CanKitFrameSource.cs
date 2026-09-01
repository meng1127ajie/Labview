using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Abstractions.SPI.Registry.Core.Endpoints;
using CanKit.Core;
using CanKit.Core.Endpoints;
using V3RttMonitor.Core.CanBus;
using KitReceiveDataView = CanKit.Abstractions.API.Common.Definitions.CanReceiveDataView;

namespace V3RttMonitor.CanAdapters;

public sealed class CanKitFrameSourceFactory : ICanFrameSourceFactory
{
    private static readonly string[] AdapterAssemblies =
    [
        "CanKit.Adapter.ZLG",
        "CanKit.Adapter.Virtual",
    ];

    private static readonly CanEndpointDescriptor[] SuggestedEndpoints =
    [
        Suggested("ZLG USBCAN-I · 设备0 通道0", "zlg://USBCAN1?index=0#ch0", "ZLG"),
        Suggested("ZLG USBCANFD-200U · 设备0 通道0", "zlg://USBCANFD-200U?index=0#ch0", "ZLG"),
        Suggested("ZLG USBCANFD-200U · 设备0 通道1", "zlg://USBCANFD-200U?index=0#ch1", "ZLG"),
        Suggested("ZLG USBCANFD-100U · 设备0 通道0", "zlg://USBCANFD-100U?index=0#ch0", "ZLG"),
        Suggested("ZLG USBCANFD-400U · 设备0 通道0", "zlg://USBCANFD-400U?index=0#ch0", "ZLG"),
        Suggested("ZLG USBCANFD-800U · 设备0 通道0", "zlg://USBCANFD-800U?index=0#ch0", "ZLG"),
        Suggested("ZLG USBCANFD-MINI · 设备0 通道0", "zlg://USBCANFD-MINI?index=0#ch0", "ZLG"),
        Suggested("ZLG PCIE-CANFD-200U · 设备0 通道0", "zlg://PCIE-CANFD-200U?index=0#ch0", "ZLG"),
        Suggested("ZLG USBCAN-II · 设备0 通道0", "zlg://USBCAN2?index=0#ch0", "ZLG"),
        Suggested("ZLG USBCAN-II · 设备0 通道1", "zlg://USBCAN2?index=0#ch1", "ZLG"),
        new CanEndpointDescriptor
        {
            ProviderId = "cankit",
            DisplayName = "虚拟CAN · JustFloat通道0（测试）",
            Endpoint = "virtual://justfloat/0",
            Transport = CanSourceTransport.Virtual,
            Detail = "无需硬件，用于验证统一接收链路",
        },
    ];

    public string Id => "cankit";
    public string DisplayName => "ZLG硬件CAN";
    public CanSourceTransport Transport => CanSourceTransport.Hardware;

    public async Task<IReadOnlyList<CanEndpointDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            PreloadAdapters();
            var results = new List<CanEndpointDescriptor>();
            try
            {
                foreach (var endpoint in BusEndpointEntry.Enumerate("zlg", "virtual"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isOnlineDevice = endpoint.Meta?.TryGetValue("status", out var status) == true && status == "0";
                    results.Add(new CanEndpointDescriptor
                    {
                        ProviderId = Id,
                        DisplayName = endpoint.Title ?? endpoint.Endpoint,
                        Endpoint = endpoint.Endpoint,
                        Transport = endpoint.Scheme.Equals("virtual", StringComparison.OrdinalIgnoreCase)
                            ? CanSourceTransport.Virtual
                            : CanSourceTransport.Hardware,
                        IsDetected = isOnlineDevice,
                        Detail = FormatMetadata(endpoint),
                    });
                }
            }
            catch
            {
                // Individual vendor SDKs may be missing. Suggestions below remain usable.
            }

            foreach (var suggestion in SuggestedEndpoints)
            {
                if (results.Any(item => item.Endpoint.Equals(suggestion.Endpoint, StringComparison.OrdinalIgnoreCase))) continue;
                results.Add(suggestion);
            }

            return (IReadOnlyList<CanEndpointDescriptor>)results
                .OrderByDescending(item => item.IsDetected)
                .ThenBy(item => item.Transport)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public ICanFrameSource Create(CanSourceConnectionOptions options)
        => new CanKitFrameSource(options);

    private static void PreloadAdapters()
    {
        foreach (var assemblyName in AdapterAssemblies)
        {
            try { Assembly.Load(new AssemblyName(assemblyName)); }
            catch { /* A missing optional adapter must not block the other providers. */ }
        }
    }

    private static string FormatMetadata(BusEndpointInfo endpoint)
        => endpoint.Meta is null || endpoint.Meta.Count == 0
            ? endpoint.Scheme
            : string.Join(" · ", endpoint.Meta.Select(item => $"{item.Key}={item.Value}"));

    private static CanEndpointDescriptor Suggested(string displayName, string endpoint, string driver)
        => new()
        {
            ProviderId = "cankit",
            DisplayName = displayName,
            Endpoint = endpoint,
            Transport = CanSourceTransport.Hardware,
            Detail = $"使用内置官方x64 {driver}运行库；连接时验证设备和Windows驱动",
        };
}

public sealed class CanKitFrameSource(CanSourceConnectionOptions settings)
    : ICanFrameSource, ICanFrameSourceDiagnostics
{
    private readonly Stopwatch _clock = new();
    private ICanBus? _bus;
    private long _receivedFrames;
    private long _receivedBytes;
    private long _errors;
    private string _adapterState = "未启动";
    private readonly int _channel = ParseChannel(settings.Endpoint);

    public string Name => settings.Endpoint;
    public bool IsRunning => Volatile.Read(ref _bus) is not null;
    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        Interlocked.Exchange(ref _receivedFrames, 0);
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _errors, 0);
        _adapterState = "正在打开";
        StatusChanged?.Invoke($"正在打开CAN适配器：{settings.Endpoint}");

        var runtime = NativeCanRuntimeResolver.Prepare(settings.Endpoint);
        StatusChanged?.Invoke($"驱动已就绪：{runtime}");
        var bus = await Task.Run(() => CanBus.Open(settings.Endpoint, Configure), cancellationToken).ConfigureAwait(false);
        bus.FrameObserved += Bus_OnFrameObserved;
        bus.FaultOccurred += Bus_OnFaultOccurred;
        bus.BackgroundExceptionOccurred += Bus_OnBackgroundException;
        Volatile.Write(ref _bus, bus);
        _clock.Restart();
        _adapterState = "在线";
        StatusChanged?.Invoke($"硬件CAN在线 · {settings.Endpoint}");
    }

    private void Configure(CanKit.Abstractions.API.Common.IBusInitOptionsConfigurator configurator)
    {
        if (settings.Protocol == CanLinkProtocol.CanFd)
            configurator.Fd(settings.NominalBitrate, settings.DataBitrate);
        else
            configurator.Baud(settings.NominalBitrate);

        configurator.SetWorkMode(settings.ListenOnly ? ChannelWorkMode.ListenOnly : ChannelWorkMode.Normal);
        configurator.SetAsyncBufferCapacity(131_072);
        if (settings.InternalTermination) configurator.InternalRes(true);
        if (settings.Endpoint.StartsWith("zlg://", StringComparison.OrdinalIgnoreCase))
            configurator.Custom("PollingInterval", 1);
    }

    private void Bus_OnFrameObserved(object? sender, KitReceiveDataView received)
    {
        try
        {
            var source = received.CanFrame;
            var data = source.Data.ToArray();
            var frame = new CanFrame
            {
                TimestampSeconds = _clock.Elapsed.TotalSeconds,
                Channel = _channel,
                Id = checked((uint)source.ID),
                IsExtended = source.IsExtendedFrame,
                Direction = received.IsEcho ? CanDirection.Tx : CanDirection.Rx,
                Kind = source.IsErrorFrame ? CanFrameKind.Error : source.IsRemoteFrame ? CanFrameKind.Remote : CanFrameKind.Data,
                IsFd = source.FrameKind == CanFrameType.CanFd,
                BitrateSwitch = source.BitRateSwitch,
                ErrorStateIndicator = source.ErrorStateIndicator,
                Dlc = source.Dlc,
                Data = data,
                SourceName = settings.Endpoint,
            };
            Interlocked.Increment(ref _receivedFrames);
            Interlocked.Add(ref _receivedBytes, data.Length);
            FrameReceived?.Invoke(frame);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _adapterState = $"帧转换异常：{exception.Message}";
            StatusChanged?.Invoke(_adapterState);
        }
    }

    private void Bus_OnFaultOccurred(object? sender, Exception exception)
    {
        Interlocked.Increment(ref _errors);
        _adapterState = $"硬件故障：{exception.Message}";
        StatusChanged?.Invoke(_adapterState);
    }

    private void Bus_OnBackgroundException(object? sender, Exception exception)
    {
        Interlocked.Increment(ref _errors);
        _adapterState = $"驱动异常：{exception.Message}";
        StatusChanged?.Invoke(_adapterState);
    }

    public async Task StopAsync()
    {
        var bus = Interlocked.Exchange(ref _bus, null);
        if (bus is null) return;
        bus.FrameObserved -= Bus_OnFrameObserved;
        bus.FaultOccurred -= Bus_OnFaultOccurred;
        bus.BackgroundExceptionOccurred -= Bus_OnBackgroundException;
        await Task.Run(bus.Dispose).ConfigureAwait(false);
        _clock.Stop();
        _adapterState = "已断开";
        StatusChanged?.Invoke("硬件CAN已断开。");
    }

    public CanFrameSourceStatistics GetStatistics() => new(
        Interlocked.Read(ref _receivedBytes),
        0,
        Interlocked.Read(ref _receivedFrames),
        Interlocked.Read(ref _errors),
        string.Empty,
        0,
        _adapterState);

    public string FormatStatistics()
    {
        var statistics = GetStatistics();
        var bytesText = statistics.ReceivedBytes >= 1024
            ? $"{statistics.ReceivedBytes / 1024.0:F1} KiB"
            : $"{statistics.ReceivedBytes} B";
        return $"硬件接收 {statistics.ParsedFrames:N0}帧 · 负载 {bytesText} · 驱动异常 {statistics.ParseErrors:N0} · {statistics.AdapterState}";
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static int ParseChannel(string endpoint)
    {
        var match = Regex.Match(endpoint, @"#ch(?<channel>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["channel"].Value, out var channel) ? channel + 1 : 1;
    }
}
