using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using V3RttMonitor.CanAdapters;
using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App.ViewModels;

public sealed class CanAnalysisViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumHistoryFrames = 1_000_000;
    private const int MaximumVisibleRows = 3_000;
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _flushTimer;
    private readonly ConcurrentQueue<CanFrame> _pendingFrames = new();
    private readonly object _historyGate = new();
    private readonly List<CanFrame> _history = [];
    private readonly Dictionary<CanFrameKey, CanMessageItemViewModel> _messageMap = [];
    private readonly Dictionary<string, CanSignalItemViewModel> _signalMap = [];
    private readonly CanLogReaderRegistry _logReaders = new();
    private readonly List<DbcSourceDatabase> _dbcSources = [];
    private readonly ICanFrameSourceFactory _hardwareSourceFactory = new CanKitFrameSourceFactory();
    private ICanFrameSource? _onlineSource;
    private DbcDatabase? _database;
    private CanMessageItemViewModel? _selectedMessage;
    private string _dbcPath = string.Empty;
    private string _dbcStatus = "未加载DBC（可查看原始字节）";
    private string _logPath = string.Empty;
    private string _sourceStatus = "选择硬件CAN或网络CAN数据源";
    private string _searchText = string.Empty;
    private string _tcpHost = "127.0.0.1";
    private int _tcpPort = 19022;
    private CanSourceModeItemViewModel? _selectedSourceMode;
    private CanEndpointDescriptor? _selectedHardwareEndpoint;
    private string _hardwareEndpoint = string.Empty;
    private int _nominalBitrate = 500_000;
    private int _dataBitrate = 2_000_000;
    private bool _useCanFd;
    private bool _listenOnly = true;
    private bool _internalTermination;
    private bool _isDiscoveringDevices;
    private string _hardwareDiscoveryStatus = "点击刷新扫描已安装的CAN适配器，也可以直接选择预置端点";
    private bool _isOnline;
    private bool _isBusy;
    private bool _favoriteOnly;
    private string _frameCountText = "0";
    private string _messageCountText = "0";
    private string _durationText = "0.000 s";
    private string _frameRateText = "0.0 fps";
    private long _historyRevision;
    private bool _isApplyingSignalSelection;
    private DbcFileItemViewModel? _selectedDbcFile;
    private string _onlineDiagnosticsText = "在线诊断：未连接";
    private string _onlineRawPreview = string.Empty;

    public CanAnalysisViewModel(CanWorkspaceMode workspaceMode = CanWorkspaceMode.Online)
    {
        WorkspaceMode = workspaceMode;
        Messages = [];
        Signals = [];
        SelectedSignals = [];
        RecentFrames = [];
        DbcFiles = [];
        SourceModes =
        [
            new CanSourceModeItemViewModel("hardware", "硬件CAN", "直接打开USB/PCIe CAN适配器"),
            new CanSourceModeItemViewModel("network", "网络CAN", "TCP文本、socketcand或网关数据源"),
        ];
        SelectedSourceMode = SourceModes[0];
        HardwareEndpoints = [];
        NominalBitrates = new[] { 50_000, 83_333, 100_000, 125_000, 250_000, 500_000, 800_000, 1_000_000 }.Select(CanBitrateOption.From).ToArray();
        DataBitrates = new[] { 500_000, 1_000_000, 2_000_000, 4_000_000, 5_000_000, 8_000_000 }.Select(CanBitrateOption.From).ToArray();
        if (IsOfflineWorkspace) SourceStatus = "等待加载BLF、ASC、LOG或CSV离线数据";
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += (_, _) => FlushPendingFrames();
        _flushTimer.Start();
    }

    public ObservableCollection<CanMessageItemViewModel> Messages { get; }
    public ObservableCollection<CanSignalItemViewModel> Signals { get; }
    public ObservableCollection<CanSignalItemViewModel> SelectedSignals { get; }
    public ObservableCollection<CanFrameRowViewModel> RecentFrames { get; }
    public ObservableCollection<DbcFileItemViewModel> DbcFiles { get; }
    public ObservableCollection<CanSourceModeItemViewModel> SourceModes { get; }
    public ObservableCollection<CanEndpointDescriptor> HardwareEndpoints { get; }
    public IReadOnlyList<CanBitrateOption> NominalBitrates { get; }
    public IReadOnlyList<CanBitrateOption> DataBitrates { get; }
    public CanWorkspaceMode WorkspaceMode { get; }
    public bool IsOnlineWorkspace => WorkspaceMode == CanWorkspaceMode.Online;
    public bool IsOfflineWorkspace => WorkspaceMode == CanWorkspaceMode.Offline;
    public DbcDatabase? Database => _database;
    public string DbcPath { get => _dbcPath; private set => SetProperty(ref _dbcPath, value); }
    public string DbcStatus { get => _dbcStatus; private set => SetProperty(ref _dbcStatus, value); }
    public string LogPath { get => _logPath; private set => SetProperty(ref _logPath, value); }
    public string SourceStatus { get => _sourceStatus; private set => SetProperty(ref _sourceStatus, value); }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string TcpHost { get => _tcpHost; set => SetProperty(ref _tcpHost, value); }
    public int TcpPort { get => _tcpPort; set => SetProperty(ref _tcpPort, value); }
    public CanSourceModeItemViewModel? SelectedSourceMode
    {
        get => _selectedSourceMode;
        set
        {
            if (!SetProperty(ref _selectedSourceMode, value)) return;
            OnPropertyChanged(nameof(IsHardwareSource));
            OnPropertyChanged(nameof(IsNetworkSource));
            if (IsOnlineWorkspace)
                SourceStatus = IsHardwareSource ? "硬件CAN：选择适配器、协议和波特率后连接" : "网络CAN：输入服务器地址和端口后连接";
        }
    }
    public bool IsHardwareSource => SelectedSourceMode?.Id == "hardware";
    public bool IsNetworkSource => SelectedSourceMode?.Id == "network";
    public CanEndpointDescriptor? SelectedHardwareEndpoint
    {
        get => _selectedHardwareEndpoint;
        set
        {
            if (!SetProperty(ref _selectedHardwareEndpoint, value) || value is null) return;
            HardwareEndpoint = value.Endpoint;
            var runtime = CanDriverRuntimeProbe.ValidateZlg();
            if (string.IsNullOrWhiteSpace(value.Endpoint))
            {
                HardwareDiscoveryStatus = runtime.IsReady
                    ? "官方x64驱动已就绪 · 请选择实际设备型号与通道 · 直连前关闭ZCANPRO"
                    : $"ZLGCAN运行库不可用：{runtime.Message}";
                return;
            }
            HardwareDiscoveryStatus = runtime.IsReady
                ? value.IsDetected
                    ? $"官方x64驱动已就绪 · 已检测：{value.DisplayName} · 直连时请关闭ZCANPRO"
                    : $"官方x64驱动已就绪 · {value.Detail} · 直连时请关闭ZCANPRO"
                : $"ZLGCAN运行库不可用：{runtime.Message}";
        }
    }
    public string HardwareEndpoint { get => _hardwareEndpoint; set => SetProperty(ref _hardwareEndpoint, value); }
    public int NominalBitrate { get => _nominalBitrate; set => SetProperty(ref _nominalBitrate, value); }
    public int DataBitrate { get => _dataBitrate; set => SetProperty(ref _dataBitrate, value); }
    public bool UseCanFd { get => _useCanFd; set => SetProperty(ref _useCanFd, value); }
    public bool ListenOnly { get => _listenOnly; set => SetProperty(ref _listenOnly, value); }
    public bool InternalTermination { get => _internalTermination; set => SetProperty(ref _internalTermination, value); }
    public bool IsDiscoveringDevices { get => _isDiscoveringDevices; private set => SetProperty(ref _isDiscoveringDevices, value); }
    public string HardwareDiscoveryStatus { get => _hardwareDiscoveryStatus; private set => SetProperty(ref _hardwareDiscoveryStatus, value); }
    public bool IsOnline { get => _isOnline; private set => SetProperty(ref _isOnline, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool FavoriteOnly { get => _favoriteOnly; set => SetProperty(ref _favoriteOnly, value); }
    public string FrameCountText { get => _frameCountText; private set => SetProperty(ref _frameCountText, value); }
    public string MessageCountText { get => _messageCountText; private set => SetProperty(ref _messageCountText, value); }
    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }
    public string FrameRateText { get => _frameRateText; private set => SetProperty(ref _frameRateText, value); }
    public long HistoryRevision => Interlocked.Read(ref _historyRevision);
    public int HistoryFrameCount { get { lock (_historyGate) return _history.Count; } }
    public DbcFileItemViewModel? SelectedDbcFile { get => _selectedDbcFile; set => SetProperty(ref _selectedDbcFile, value); }
    public string OnlineDiagnosticsText { get => _onlineDiagnosticsText; private set => SetProperty(ref _onlineDiagnosticsText, value); }
    public string OnlineRawPreview { get => _onlineRawPreview; private set => SetProperty(ref _onlineRawPreview, value); }

    public CanMessageItemViewModel? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (SetProperty(ref _selectedMessage, value)) RebuildVisibleSignals();
        }
    }

    public event EventHandler<CanFramesChangedEventArgs>? FramesChanged;
    public event EventHandler? SignalSelectionChanged;
    public event EventHandler? CatalogChanged;

    public CanFrame[] GetFramesSnapshot()
    {
        lock (_historyGate) return _history.ToArray();
    }

    public async Task LoadDbcAsync(string path, CancellationToken cancellationToken = default)
        => await LoadDbcFilesAsync([path], cancellationToken);

    public async Task LoadDbcFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        var requested = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        DbcStatus = $"正在解析 {requested.Length} 个DBC…";
        try
        {
            var parseErrors = 0;
            foreach (var path in requested)
            {
                var result = await DbcParser.ParseFileAsync(path, cancellationToken);
                parseErrors += result.Diagnostics.Count(item => item.IsError);
                _dbcSources.RemoveAll(source => source.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                _dbcSources.Add(new(Path.GetFileNameWithoutExtension(path), path, result.Database));
            }
            RebuildMergedDatabase(parseErrors);
        }
        catch (Exception exception)
        {
            DbcStatus = $"DBC加载失败：{exception.Message}";
        }
        finally { IsBusy = false; }
    }

    public void ApplyDatabase(DbcDatabase database, string? path = null)
    {
        _dbcSources.Clear();
        var sourcePath = path ?? string.Empty;
        _dbcSources.Add(new(string.IsNullOrWhiteSpace(path) ? database.Name : Path.GetFileNameWithoutExtension(path), sourcePath, database));
        RebuildMergedDatabase();
    }

    public void RemoveSelectedDbc()
    {
        if (SelectedDbcFile is null) return;
        _dbcSources.RemoveAll(source => source.Path.Equals(SelectedDbcFile.Path, StringComparison.OrdinalIgnoreCase) && source.Name == SelectedDbcFile.Name);
        RebuildMergedDatabase();
    }

    private void RebuildMergedDatabase(int parseErrors = 0)
    {
        var merge = DbcDatabaseMerger.Merge(_dbcSources);
        _database = _dbcSources.Count == 0 ? null : merge.Database;
        DbcFiles.Clear();
        foreach (var source in _dbcSources)
        {
            var item = new DbcFileItemViewModel(source.Name, source.Path, source.Database.Messages.Count, source.Database.Messages.Sum(message => message.Signals.Count));
            DbcFiles.Add(item);
        }
        SelectedDbcFile = DbcFiles.LastOrDefault();
        DbcPath = string.Join("; ", _dbcSources.Select(source => source.Path));
        DbcStatus = _database is null
            ? "未加载DBC（可查看原始字节）"
            : $"{DbcFiles.Count}个DBC · {_database.Messages.Count:N0}报文 · {_database.Messages.Sum(item => item.Signals.Count):N0}信号"
                + (merge.Conflicts.Count > 0 ? $" · {merge.Conflicts.Count}个ID冲突（后加载优先）" : string.Empty)
                + (parseErrors > 0 ? $" · {parseErrors}处需检查" : string.Empty);
        foreach (var catalogOnly in Messages.Where(item => !item.HasReceived).ToArray())
        {
            Messages.Remove(catalogOnly);
            _messageMap.Remove(catalogOnly.Key);
            if (ReferenceEquals(SelectedMessage, catalogOnly)) SelectedMessage = null;
        }
        foreach (var messageItem in Messages)
        {
            var definition = _database?.FindMessage(messageItem.Id, messageItem.IsExtended);
            messageItem.Name = definition?.Name ?? "未定义报文";
            messageItem.IsDbcDefined = definition is not null;
        }
        if (_database is not null)
        {
            foreach (var definition in _database.Messages)
            {
                if (_messageMap.ContainsKey(definition.Key)) continue;
                var catalogItem = new CanMessageItemViewModel(definition.Key) { Name = definition.Name, IsDbcDefined = true };
                _messageMap[definition.Key] = catalogItem;
                Messages.Add(catalogItem);
            }
        }
        foreach (var signal in _signalMap.Values) signal.PropertyChanged -= Signal_OnPropertyChanged;
        _signalMap.Clear();
        SelectedSignals.Clear();
        RebuildVisibleSignals();
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CanLogLoadResult?> LoadLogAsync(string path, CancellationToken cancellationToken = default)
    {
        var results = await LoadLogsAsync([path], CanLogMergeMode.Replace, 0, cancellationToken);
        return results.FirstOrDefault();
    }

    public async Task<IReadOnlyList<CanLogLoadResult>> LoadLogsAsync(
        IEnumerable<string> paths,
        CanLogMergeMode mergeMode,
        double gapSeconds,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        var requested = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SourceStatus = $"正在读取 {requested.Length} 个CAN日志…";
        try
        {
            await StopOnlineAsync();
            var progress = new Progress<CanLogLoadProgress>(value => SourceStatus = $"正在读取：{value.FramesRead:N0}帧 · 扫描{value.LinesRead:N0}行");
            var results = new List<CanLogLoadResult>();
            foreach (var path in requested)
            {
                SourceStatus = $"正在读取：{Path.GetFileName(path)}";
                results.Add(await _logReaders.Resolve(path).ReadAsync(path, progress, cancellationToken));
            }
            var existing = GetFramesSnapshot();
            var segments = results.Select(result => new CanLogSegment(Path.GetFileName(result.Path), result.Frames)).ToArray();
            var merged = CanLogMerger.Merge(existing, segments, mergeMode, gapSeconds);
            ReplaceFrames(merged);
            LogPath = string.Join("; ", requested);
            var diagnostics = results.Sum(result => result.Diagnostics.Count);
            SourceStatus = $"{results.Count}段日志 · {merged.Count:N0}帧 · {FormatDuration(merged)}"
                + (diagnostics > 0 ? $" · {diagnostics}条兼容性提示" : string.Empty);
            return results;
        }
        catch (Exception exception)
        {
            SourceStatus = $"日志加载失败：{exception.Message}";
            return [];
        }
        finally { IsBusy = false; }
    }

    private static string FormatDuration(IReadOnlyList<CanFrame> frames)
    {
        if (frames.Count < 2) return "0.000s";
        return $"{Math.Max(0, frames[^1].TimestampSeconds - frames[0].TimestampSeconds):F3}s";
    }

    public async Task DiscoverHardwareAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOnlineWorkspace) return;
        if (IsDiscoveringDevices) return;
        IsDiscoveringDevices = true;
        HardwareDiscoveryStatus = "正在扫描已安装驱动和可发现CAN通道…";
        try
        {
            var endpoints = await _hardwareSourceFactory.DiscoverAsync(cancellationToken);
            HardwareEndpoints.Clear();
            var placeholder = new CanEndpointDescriptor
            {
                ProviderId = _hardwareSourceFactory.Id,
                DisplayName = "请选择ZLG设备型号与通道…",
                Endpoint = string.Empty,
                Transport = CanSourceTransport.Hardware,
            };
            HardwareEndpoints.Add(placeholder);
            foreach (var endpoint in endpoints) HardwareEndpoints.Add(endpoint);
            var detectedCount = endpoints.Count(item => item.IsDetected);
            var runtime = CanDriverRuntimeProbe.ValidateZlg();
            HardwareDiscoveryStatus = !runtime.IsReady
                ? $"官方x64 ZLGCAN运行库不可用：{runtime.Message}"
                : detectedCount > 0
                    ? $"官方x64驱动已就绪 · 检测到 {detectedCount} 个在线通道"
                    : "官方x64驱动已就绪 · 请选择设备型号、设备索引和通道后连接";
            SelectedHardwareEndpoint = endpoints.FirstOrDefault(item => item.IsDetected) ?? placeholder;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            HardwareDiscoveryStatus = "设备扫描已取消";
        }
        catch (Exception exception)
        {
            HardwareDiscoveryStatus = $"设备扫描失败：{exception.Message}；仍可手动输入端点";
        }
        finally { IsDiscoveringDevices = false; }
    }

    public async Task StartOnlineAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOnlineWorkspace) throw new InvalidOperationException("离线分析会话不能启动硬件连接。");
        if (IsOnline) return;
        IsBusy = true;
        SourceStatus = IsHardwareSource
            ? $"正在打开硬件CAN：{HardwareEndpoint}…"
            : $"正在连接网络CAN {TcpHost}:{TcpPort}…";
        try
        {
            ICanFrameSource source;
            if (IsHardwareSource)
            {
                if (string.IsNullOrWhiteSpace(HardwareEndpoint)) throw new InvalidOperationException("请先选择或输入硬件CAN端点。");
                source = _hardwareSourceFactory.Create(new CanSourceConnectionOptions
                {
                    Endpoint = HardwareEndpoint.Trim(),
                    Protocol = UseCanFd ? CanLinkProtocol.CanFd : CanLinkProtocol.Classic,
                    NominalBitrate = NominalBitrate,
                    DataBitrate = DataBitrate,
                    ListenOnly = ListenOnly,
                    InternalTermination = InternalTermination,
                });
            }
            else
            {
                source = new TcpCanFrameSource(TcpHost, TcpPort);
            }

            _onlineSource = source;
            source.FrameReceived += OnlineFrameReceived;
            source.StatusChanged += OnlineStatusChanged;
            await source.StartAsync(cancellationToken);
            IsOnline = true;
            SourceStatus = IsHardwareSource
                ? $"硬件CAN在线 · {HardwareEndpoint}"
                : $"网络CAN在线 · {TcpHost}:{TcpPort}";
            UpdateOnlineDiagnostics();
        }
        catch (Exception exception)
        {
            SourceStatus = $"CAN连接失败：{exception.Message}";
            var failedSource = Interlocked.Exchange(ref _onlineSource, null);
            if (failedSource is not null) await failedSource.DisposeAsync();
            OnlineDiagnosticsText = "在线诊断：连接未建立";
        }
        finally { IsBusy = false; }
    }

    public async Task StopOnlineAsync()
    {
        var source = Interlocked.Exchange(ref _onlineSource, null);
        if (source is null) { IsOnline = false; return; }
        source.FrameReceived -= OnlineFrameReceived;
        source.StatusChanged -= OnlineStatusChanged;
        await source.DisposeAsync();
        IsOnline = false;
        SourceStatus = "在线CAN已断开";
        OnlineDiagnosticsText = "在线诊断：未连接";
        OnlineRawPreview = string.Empty;
    }

    private void OnlineFrameReceived(CanFrame frame) => _pendingFrames.Enqueue(frame);
    private void OnlineStatusChanged(string status) => _dispatcher.BeginInvoke(() => SourceStatus = status);

    private void FlushPendingFrames()
    {
        UpdateOnlineDiagnostics();
        if (_pendingFrames.IsEmpty) return;
        var batch = new List<CanFrame>(4096);
        while (batch.Count < 20_000 && _pendingFrames.TryDequeue(out var frame)) batch.Add(frame);
        AppendFrames(batch);
    }

    private void UpdateOnlineDiagnostics()
    {
        if (_onlineSource is not ICanFrameSourceDiagnostics diagnostics) return;
        var statistics = diagnostics.GetStatistics();
        OnlineRawPreview = statistics.LastRawPreview;
        OnlineDiagnosticsText = diagnostics.FormatStatistics();
    }

    public void ReplaceFrames(IEnumerable<CanFrame> frames)
    {
        lock (_historyGate) _history.Clear();
        RecentFrames.Clear();
        foreach (var item in Messages) item.ResetStatistics();
        _messageMap.Clear();
        Messages.Clear();
        AppendFrames(frames, reset: true);
        AddMissingDbcCatalogMessages();
    }

    private void AddMissingDbcCatalogMessages()
    {
        if (_database is null) return;
        foreach (var definition in _database.Messages)
        {
            if (_messageMap.ContainsKey(definition.Key)) continue;
            var item = new CanMessageItemViewModel(definition.Key) { Name = definition.Name, IsDbcDefined = true };
            _messageMap[definition.Key] = item;
            Messages.Add(item);
        }
        if (SelectedMessage is null) SelectedMessage = Messages.FirstOrDefault();
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearFrames()
    {
        ReplaceFrames([]);
        SourceStatus = IsOnline
            ? "在线CAN保持连接 · 接收缓存已清空"
            : IsOfflineWorkspace ? "离线数据集已关闭，DBC配置保留" : "接收缓存已清空，DBC配置保留";
    }

    private void AppendFrames(IEnumerable<CanFrame> frames, bool reset = false)
    {
        var batch = frames as IReadOnlyList<CanFrame> ?? frames.ToArray();
        if (batch.Count == 0)
        {
            if (reset)
            {
                Interlocked.Increment(ref _historyRevision);
                FramesChanged?.Invoke(this, new CanFramesChangedEventArgs([], true));
            }
            UpdateStatistics();
            return;
        }
        lock (_historyGate)
        {
            _history.AddRange(batch);
            if (_history.Count > MaximumHistoryFrames) _history.RemoveRange(0, _history.Count - MaximumHistoryFrames);
        }

        var recentStart = Math.Max(0, batch.Count - MaximumVisibleRows);
        for (var frameIndex = 0; frameIndex < batch.Count; frameIndex++)
        {
            var frame = batch[frameIndex];
            if (!_messageMap.TryGetValue(frame.Key, out var messageItem))
            {
                messageItem = new CanMessageItemViewModel(frame.Key)
                {
                    Name = _database?.FindMessage(frame.Id, frame.IsExtended)?.Name ?? "未定义报文",
                    IsDbcDefined = _database?.FindMessage(frame.Id, frame.IsExtended) is not null,
                };
                _messageMap[frame.Key] = messageItem;
                Messages.Add(messageItem);
                if (SelectedMessage is null) SelectedMessage = messageItem;
            }
            messageItem.Observe(frame);
            foreach (var signal in Signals.Where(signal => signal.Message.Key == frame.Key)) signal.FrequencyText = messageItem.RateText;
            var messageName = _database?.FindMessage(frame.Id, frame.IsExtended)?.Name ?? messageItem.Name;
            if (frameIndex >= recentStart) RecentFrames.Add(new CanFrameRowViewModel(frame, messageName));
        }
        while (RecentFrames.Count > MaximumVisibleRows) RecentFrames.RemoveAt(0);
        Interlocked.Increment(ref _historyRevision);
        if (SelectedMessage is not null
            && _database?.FindMessage(SelectedMessage.Id, SelectedMessage.IsExtended) is null
            && Signals.Count < SelectedMessage.PayloadLength)
        {
            RebuildVisibleSignals();
        }
        UpdateLatestSignalValues(batch);
        UpdateStatistics();
        FramesChanged?.Invoke(this, new CanFramesChangedEventArgs(batch, reset));
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateLatestSignalValues(IReadOnlyList<CanFrame> frames)
    {
        var displaySignals = Signals.Concat(SelectedSignals).Distinct().ToArray();
        if (displaySignals.Length == 0) return;
        var signalsByMessage = displaySignals.GroupBy(item => item.Message.Key).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var frame in frames)
        {
            if (!signalsByMessage.TryGetValue(frame.Key, out var signals)) continue;
            foreach (var signalItem in signals)
            {
                if (DbcCodec.TryDecode(signalItem.Message, signalItem.Signal, frame.Data, out var decoded))
                {
                    signalItem.Value = decoded.PhysicalValue;
                    signalItem.ChoiceText = decoded.ChoiceText;
                }
            }
        }
    }

    private void UpdateStatistics()
    {
        int frameCount;
        double firstTimestamp = 0;
        double lastTimestamp = 0;
        lock (_historyGate)
        {
            frameCount = _history.Count;
            if (frameCount > 0)
            {
                firstTimestamp = _history[0].TimestampSeconds;
                lastTimestamp = _history[^1].TimestampSeconds;
            }
        }
        FrameCountText = frameCount.ToString("N0");
        MessageCountText = Messages.Count.ToString("N0");
        if (frameCount < 2)
        {
            DurationText = "0.000 s";
            FrameRateText = "0.0 fps";
            return;
        }
        var duration = Math.Max(0, lastTimestamp - firstTimestamp);
        DurationText = $"{duration:F3} s";
        FrameRateText = duration <= 0 ? "-" : $"{(frameCount - 1) / duration:F1} fps";
    }

    private void RebuildVisibleSignals()
    {
        Signals.Clear();
        if (SelectedMessage is null) return;
        var dbcMessage = _database?.FindMessage(SelectedMessage.Id, SelectedMessage.IsExtended);
        var message = dbcMessage ?? new DbcMessage
        {
            Id = SelectedMessage.Id,
            IsExtended = SelectedMessage.IsExtended,
            Name = SelectedMessage.Name,
            Length = Math.Max(1, SelectedMessage.PayloadLength),
        };
        var signalDefinitions = dbcMessage?.Signals.Count > 0
            ? dbcMessage.Signals.Select(signal => (Signal: signal, Raw: false)).ToArray()
            : Enumerable.Range(0, Math.Clamp(Math.Max(SelectedMessage.PayloadLength, 8), 1, 64))
                .Select(index => (new DbcSignal { Name = $"Byte{index}", StartBit = index * 8, Length = 8, ByteOrder = DbcByteOrder.Intel, Maximum = 255, Unit = "raw" }, true)).ToArray();
        var color = 0;
        foreach (var (signal, raw) in signalDefinitions)
        {
            var stable = $"{message.Id:X8}:{message.IsExtended}:{signal.Name}";
            if (!_signalMap.TryGetValue(stable, out var item))
            {
                item = new CanSignalItemViewModel(message, signal, color++, raw)
                {
                    PlotGroup = 1,
                    FrequencyText = _messageMap.TryGetValue(message.Key, out var stats) ? stats.RateText : "-",
                };
                item.PropertyChanged += Signal_OnPropertyChanged;
                _signalMap[stable] = item;
            }
            Signals.Add(item);
        }
        CanFrame? latest = null;
        lock (_historyGate)
        {
            for (var index = _history.Count - 1; index >= 0; index--)
            {
                if (_history[index].Key != message.Key) continue;
                latest = _history[index];
                break;
            }
        }
        if (latest is not null) UpdateLatestSignalValues([latest]);
    }

    public void NotifySignalSelectionChanged()
    {
        if (_isApplyingSignalSelection) return;
        var selected = _signalMap.Values.Where(item => item.IsPlotted).ToArray();
        SelectedSignals.Clear();
        foreach (var signal in selected) SelectedSignals.Add(signal);
        SignalSelectionChanged?.Invoke(this, EventArgs.Empty);
        UpdateLatestSignalValues(GetFramesSnapshot().TakeLast(1000).ToArray());
    }

    public void ClearSignalSelection()
    {
        _isApplyingSignalSelection = true;
        foreach (var signal in _signalMap.Values) signal.IsPlotted = false;
        _isApplyingSignalSelection = false;
        NotifySignalSelectionChanged();
    }

    private void Signal_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isApplyingSignalSelection || sender is not CanSignalItemViewModel signal) return;
        if (e.PropertyName == nameof(CanSignalItemViewModel.PlotGroup))
        {
            SignalSelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (e.PropertyName != nameof(CanSignalItemViewModel.IsPlotted)) return;
        if (signal.IsPlotted && _signalMap.Values.Count(item => item.IsPlotted) > 8)
        {
            _isApplyingSignalSelection = true;
            signal.IsPlotted = false;
            _isApplyingSignalSelection = false;
        }
        NotifySignalSelectionChanged();
    }


    public async ValueTask DisposeAsync()
    {
        _flushTimer.Stop();
        foreach (var signal in _signalMap.Values) signal.PropertyChanged -= Signal_OnPropertyChanged;
        await StopOnlineAsync();
    }
}

public sealed class CanFramesChangedEventArgs(IReadOnlyList<CanFrame> frames, bool isReset) : EventArgs
{
    public IReadOnlyList<CanFrame> Frames { get; } = frames;
    public bool IsReset { get; } = isReset;
}

public sealed record CanSourceModeItemViewModel(string Id, string DisplayName, string Description)
{
    public override string ToString() => DisplayName;
}

public sealed record CanBitrateOption(int Value, string DisplayName)
{
    public static CanBitrateOption From(int value)
        => new(value, value >= 1_000_000 ? $"{value / 1_000_000.0:G} Mbit/s" : $"{value / 1_000.0:G} kbit/s");
    public override string ToString() => DisplayName;
}

public enum CanWorkspaceMode
{
    Online,
    Offline,
}
