using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.App.Services;
using V3RttMonitor.Core.Diagnostics;
using V3RttMonitor.Core.Hss;
using V3RttMonitor.Core.Protocol;
using V3RttMonitor.Core.Transport;

namespace V3RttMonitor.App.ViewModels;

public enum ConnectionMode { JLinkRtt, TcpDirect, Hss }

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumAnalysisFrames = 200_000;
    private const int TrimChunk = 10_000;

    private readonly RttSession _session = new();
    private readonly JLinkHssSession _hssSession = new();
    private IHssSession? _activeHssSession;
    private readonly ElfSymbolReader _elfReader = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly object _historyGate = new();
    private readonly List<RttFrame> _analysisFrames = [];
    private readonly Queue<string> _logLines = new();
    private RttFrame? _latestFrame;
    private readonly Dictionary<int, SignalTemplateDefinition> _template = [];
    private int _pendingFloatCount;
    private bool _isSessionRunning;
    private ConnectionMode? _activeConnectionMode;
    private bool _offlineMode;
    private long _offlineFrameCount;
    private long _offlineLostFrames;
    private long _offlineGapEvents;
    private long _offlineSequenceAnomalies;
    private long _offlineRestarts;
    private long _offlineSequenceStep;
    private bool _offlineSequenceStepConfirmed;
    private long _offlineLastSequence;
    private double _offlineFrameRate;
    private double _offlineRecentFrameRate;
    private bool _isApplyingSignalLayout;
    private string _serverIp = "127.0.0.1";
    private int _serverPort = 19021;
    private string _handshakeData = "plot0";
    private int _expectedFloatCount;
    private ConnectionMode _connectionMode = ConnectionMode.JLinkRtt;
    private string _jLinkDirectory = @"C:\Program Files\SEGGER\JLink_V952";
    private string _device = "STM32G431RB";
    private int _speedKhz = 4000;
    private int _rttPort = 19021;
    private string _templateStatusText = "未加载Excel（仅CH_N）";
    private string _connectionStatus = "未连接";
    private string _connectionDetail = "J-Link RTT等待连接";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
    private Brush _dataStatusBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    private string _dataStatusText = "无数据";
    private bool _isDataFlashing;
    private string _frameCountText = "0";
    private string _frameRateText = "0.0 Hz";
    private string _lostFrameText = "0";
    private string _lostRateText = "0.000 %";
    private string _sequenceHealthText = "异常 0 · 重启 0";
    private string _sequenceText = "-";
    private string _throughputText = "0.0 KiB/s";
    private string _schemaText = "自动检测";
    private string _recordButtonText = "开始记录";
    private string _logText = string.Empty;
    private string _hssElfPath = string.Empty;
    private string _hssElfStatus = "未加载ELF";
    private string _hssVariableStatus = "未选择变量";
    private int _hssSampleRateHz = 1000;
    private ElfSymbolCatalog? _hssCatalog;
    private IReadOnlyList<HssVariableSelection> _hssVariables = [];
    private DateTime _lastHssActivityUtc = DateTime.MinValue;

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Fields = [];
        SelectedFields = [];
        // Do not fabricate a channel catalog before the stream or an Excel template
        // defines it. The binary protocol carries values and a tail marker, not names.
        BuildFieldCatalog(0, useDefaults: false);

        _session.FrameReceived += OnFrameReceived;
        _session.StateChanged += state => _dispatcher.BeginInvoke(() => ApplyConnectionState(state));
        _session.LogReceived += message => _dispatcher.BeginInvoke(() => AppendLog(message));
        _session.DataActivityChanged += active => _dispatcher.BeginInvoke(() => UpdateDataStatus(active));
        _hssSession.SampleReceived += OnHssSampleReceived;
        _hssSession.LogReceived += message => _dispatcher.BeginInvoke(() => AppendLog(message));
        _hssSession.StateChanged += active => _dispatcher.BeginInvoke(() => ApplyHssState(active));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_isSessionRunning);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => _isSessionRunning);
        RecordCommand = new RelayCommand(ToggleRecording, () => _isSessionRunning && _activeConnectionMode != ConnectionMode.Hss);
        ClearHistoryCommand = new RelayCommand(ClearData);
        LoadBinaryCommand = new AsyncRelayCommand(LoadBinaryAsync, () => !_isSessionRunning);
        LoadTemplateCommand = new AsyncRelayCommand(LoadTemplateAsync);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _refreshTimer.Tick += (_, _) => RefreshDisplay();
        _refreshTimer.Start();
    }

    public ObservableCollection<FieldValueViewModel> Fields { get; }
    public ObservableCollection<FieldValueViewModel> SelectedFields { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand RecordCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public AsyncRelayCommand LoadBinaryCommand { get; }
    public AsyncRelayCommand LoadTemplateCommand { get; }

    public string ServerIp { get => _serverIp; set => SetProperty(ref _serverIp, value); }
    public int ServerPort { get => _serverPort; set => SetProperty(ref _serverPort, value); }
    public string HandshakeData { get => _handshakeData; set => SetProperty(ref _handshakeData, value); }
    public int ExpectedFloatCount { get => _expectedFloatCount; set => SetProperty(ref _expectedFloatCount, value); }
    public ConnectionMode ConnectionMode
    {
        get => _connectionMode;
        set
        {
            if (SetProperty(ref _connectionMode, value)) OnPropertyChanged(nameof(ConnectionModeDisplayName));
        }
    }
    public string ConnectionModeDisplayName => ConnectionMode switch
    {
        ConnectionMode.JLinkRtt => "J-Link RTT",
        ConnectionMode.TcpDirect => "TCP直连",
        ConnectionMode.Hss => "J-Link HSS",
        _ => "数据采集",
    };
    public string JLinkDirectory
    {
        get => _jLinkDirectory;
        set
        {
            if (SetProperty(ref _jLinkDirectory, value)) OnPropertyChanged(nameof(HssDllPath));
        }
    }
    public string Device { get => _device; set => SetProperty(ref _device, value); }
    public int SpeedKhz { get => _speedKhz; set => SetProperty(ref _speedKhz, value); }
    public int RttPort { get => _rttPort; set => SetProperty(ref _rttPort, value); }
    public string TemplateStatusText { get => _templateStatusText; private set => SetProperty(ref _templateStatusText, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string ConnectionDetail { get => _connectionDetail; private set => SetProperty(ref _connectionDetail, value); }
    public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }
    public Brush DataStatusBrush { get => _dataStatusBrush; private set => SetProperty(ref _dataStatusBrush, value); }
    public string DataStatusText { get => _dataStatusText; private set => SetProperty(ref _dataStatusText, value); }
    public bool IsDataFlashing { get => _isDataFlashing; private set => SetProperty(ref _isDataFlashing, value); }
    public string FrameCountText { get => _frameCountText; private set => SetProperty(ref _frameCountText, value); }
    public string FrameRateText { get => _frameRateText; private set => SetProperty(ref _frameRateText, value); }
    public string LostFrameText { get => _lostFrameText; private set => SetProperty(ref _lostFrameText, value); }
    public string LostRateText { get => _lostRateText; private set => SetProperty(ref _lostRateText, value); }
    public string SequenceHealthText { get => _sequenceHealthText; private set => SetProperty(ref _sequenceHealthText, value); }
    public string SequenceText { get => _sequenceText; private set => SetProperty(ref _sequenceText, value); }
    public string ThroughputText { get => _throughputText; private set => SetProperty(ref _throughputText, value); }
    public string SchemaText { get => _schemaText; private set => SetProperty(ref _schemaText, value); }
    public string RecordButtonText { get => _recordButtonText; private set => SetProperty(ref _recordButtonText, value); }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }
    public string HssElfPath { get => _hssElfPath; private set => SetProperty(ref _hssElfPath, value); }
    public string HssElfStatus { get => _hssElfStatus; private set => SetProperty(ref _hssElfStatus, value); }
    public string HssVariableStatus { get => _hssVariableStatus; private set => SetProperty(ref _hssVariableStatus, value); }
    public int HssSampleRateHz { get => _hssSampleRateHz; set => SetProperty(ref _hssSampleRateHz, value); }
    public string HssDllPath => Path.Combine(JLinkDirectory, "JLink_x64.dll");
    public ElfSymbolCatalog? HssCatalog => _hssCatalog;
    public IReadOnlyList<HssVariableSelection> HssVariables => _hssVariables;
    public bool IsConnected => _isSessionRunning;

    public event EventHandler? AnalysisDataChanged;
    public event EventHandler? SignalLayoutChanged;

    public RttFrame[] GetAnalysisFramesSnapshot()
    {
        lock (_historyGate) return _analysisFrames.ToArray();
    }

    public void NotifySignalLayoutChanged()
    {
        RefreshSelectedFields();
        SignalLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearAllSignals()
    {
        _isApplyingSignalLayout = true;
        try
        {
            foreach (var field in Fields) field.IsPlotted = false;
        }
        finally
        {
            _isApplyingSignalLayout = false;
        }
        RefreshSelectedFields();
        SignalLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadHssElfFileAsync(string path)
    {
        HssElfStatus = "正在解析ELF…";
        try
        {
            var catalog = await _elfReader.ReadAsync(path);
            _hssCatalog = catalog;
            HssElfPath = catalog.ElfPath;
            HssElfStatus = $"RAM对象 {catalog.Symbols.Count:N0} · 标量 {catalog.Symbols.Count(item => item.IsScalarCandidate):N0}";
            _hssVariables = [];
            HssVariableStatus = "未选择变量";
            AppendLog($"ELF解析完成：{catalog.ElfPath}，RAM对象{catalog.Symbols.Count:N0}。");
        }
        catch (Exception exception)
        {
            _hssCatalog = null;
            HssElfStatus = $"ELF错误：{exception.Message}";
            AppendLog(HssElfStatus);
        }
    }

    public void ApplyHssVariables(IReadOnlyList<HssVariableSelection> variables)
    {
        _hssVariables = variables.ToArray();
        HssVariableStatus = $"已选择 {_hssVariables.Count:N0} 个变量";
        BuildHssFieldCatalog();
        AppendLog($"HSS变量已更新：{string.Join(", ", _hssVariables.Select(item => item.Name))}");
    }

    public bool ValidateHssEnvironment()
    {
        var errors = new List<string>();
        try { errors.AddRange(JLinkHssCompatibility.ValidateExports(HssDllPath)); }
        catch (Exception exception) { errors.Add($"DLL检查失败：{exception.Message}"); }
        if (_hssCatalog is null) errors.Add("尚未解析ELF");
        if (_hssVariables.Count == 0) errors.Add("尚未选择变量");
        if (HssSampleRateHz <= 0) errors.Add("采样频率必须大于0");
        HssVariableStatus = errors.Count == 0
            ? $"HSS环境通过 · {_hssVariables.Count}变量 · {HssSampleRateHz}Hz"
            : string.Join("；", errors);
        AppendLog($"HSS环境检查：{HssVariableStatus}");
        return errors.Count == 0;
    }

    public async ValueTask DisposeAsync()
    {
        _refreshTimer.Stop();
        await _session.DisposeAsync();
        await _hssSession.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            _offlineMode = false;
            ClearData();
            if (ConnectionMode == ConnectionMode.Hss)
            {
                if (_hssCatalog is null) throw new InvalidOperationException("请先选择并解析ELF文件。");
                if (_hssVariables.Count == 0) throw new InvalidOperationException("请先选择HSS变量。");
                if (HssSampleRateHz <= 0) throw new InvalidOperationException("HSS采样频率必须大于0。");
                if (!ValidateHssEnvironment()) throw new InvalidOperationException(HssVariableStatus);
                BuildHssFieldCatalog();
                await _hssSession.StartAsync(new HssConfiguration
                {
                    DllPath = HssDllPath,
                    Device = Device.Trim(),
                    SpeedKhz = SpeedKhz,
                    PeriodUs = Math.Max(1, (int)Math.Round(1_000_000.0 / HssSampleRateHz)),
                    Variables = _hssVariables,
                });
                _activeHssSession = _hssSession;
                _activeConnectionMode = ConnectionMode.Hss;
                _isSessionRunning = true;
                ConnectionStatus = "HSS在线";
                ConnectionDetail = $"{_hssVariables.Count}变量 · {HssSampleRateHz}Hz";
                StatusBrush = Brush(52, 211, 153);
                DataStatusText = "等待HSS样本";
                RaiseCommandStates();
                return;
            }
            var isJLink = ConnectionMode == ConnectionMode.JLinkRtt;
            var settings = new RttSessionSettings
            {
                Mode = isJLink ? TransportMode.JLinkRtt : TransportMode.TcpDirect,
                Host = isJLink ? "127.0.0.1" : ServerIp.Trim(),
                Port = isJLink ? RttPort : ServerPort,
                HandshakeData = isJLink ? string.Empty : HandshakeData,
                ExpectedFloatCount = ExpectedFloatCount,
                JLinkDirectory = JLinkDirectory,
                Device = Device.Trim(),
                SpeedKhz = SpeedKhz,
            };
            await _session.StartAsync(settings);
            _activeConnectionMode = ConnectionMode;
            _isSessionRunning = true;
            RaiseCommandStates();
            AppendLog($"{(isJLink ? "J-Link RTT独立" : "TCP直连")}：{settings.Host}:{settings.Port}，握手={FormatHandshake(settings.HandshakeData)}，通道数={(settings.ExpectedFloatCount == 0 ? "自动" : settings.ExpectedFloatCount)}");
        }
        catch (Exception ex)
        {
            ConnectionStatus = "连接失败";
            ConnectionDetail = ex.Message;
            StatusBrush = Brush(248, 113, 113);
            AppendLog($"连接失败：{ex.Message}");
        }
    }

    private async Task DisconnectAsync()
    {
        if (_activeConnectionMode == ConnectionMode.Hss && _activeHssSession is not null) await _activeHssSession.StopAsync();
        else await _session.StopAsync();
        _activeHssSession = null;
        _isSessionRunning = false;
        _activeConnectionMode = null;
        RaiseCommandStates();
    }

    private void ToggleRecording()
    {
        if (_session.GetStatistics().IsRecording)
        {
            _session.StopRecording();
            RecordButtonText = "开始记录";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "保存原始TCP数据",
            Filter = "JustFloat原始数据 (*.bin)|*.bin",
            FileName = $"JustFloat_{DateTime.Now:yyyyMMdd_HHmmss}.bin",
        };
        if (dialog.ShowDialog() == true)
        {
            _session.StartRecording(dialog.FileName);
            RecordButtonText = "停止记录";
        }
    }

    private async Task LoadBinaryAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "加载JustFloat BIN",
            Filter = "JustFloat原始数据 (*.bin)|*.bin|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true) await LoadBinaryFileAsync(dialog.FileName);
    }

    public async Task LoadBinaryFileAsync(string path)
    {
        var result = await Task.Run(() => ParseBinaryFile(path));
        lock (_historyGate)
        {
            _analysisFrames.Clear();
            _analysisFrames.AddRange(result.Frames.TakeLast(MaximumAnalysisFrames));
            _latestFrame = result.Frames.LastOrDefault();
        }
        _offlineMode = true;
        _offlineFrameCount = result.TotalFrames;
        _offlineLostFrames = result.LostFrames;
        _offlineGapEvents = result.GapEvents;
        _offlineSequenceAnomalies = result.SequenceAnomalies;
        _offlineRestarts = result.Restarts;
        _offlineSequenceStep = result.SequenceStep;
        _offlineSequenceStepConfirmed = result.IsSequenceStepConfirmed;
        _offlineLastSequence = result.LastSequence;
        _offlineFrameRate = result.AverageFrameRate;
        _offlineRecentFrameRate = result.RecentFrameRate;
        if (result.FloatCount > 0 && result.FloatCount != Fields.Count)
        {
            BuildFieldCatalog(result.FloatCount, useDefaults: false);
        }
        SchemaText = $"{result.FloatCount} float · {result.FloatCount * 4 + 4} bytes/帧";
        ConnectionStatus = "BIN离线";
        ConnectionDetail = $"{Path.GetFileName(path)} · {result.TotalFrames:N0}帧";
        StatusBrush = Brush(52, 211, 153);
        DataStatusText = "离线数据已加载";
        DataStatusBrush = Brush(52, 211, 153);
        AnalysisDataChanged?.Invoke(this, EventArgs.Empty);
        RefreshDisplay();
    }

    private BinaryLoadResult ParseBinaryFile(string path)
    {
        var parser = new JustFloatParser();
        var continuity = new SequenceContinuityTracker();
        parser.SetFloatCount(ExpectedFloatCount);
        var retained = new List<RttFrame>();
        long total = 0;
        long positiveTimeIntervals = 0;
        double positiveElapsedMs = 0;
        double? previousTimeMs = null;
        var recentTimesMs = new Queue<double>();
        var buffer = new byte[64 * 1024];
        using var stream = File.OpenRead(path);
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            foreach (var frame in parser.Feed(buffer.AsSpan(0, read)))
            {
                continuity.Observe(frame.Sequence);
                if (previousTimeMs is double previousTime)
                {
                    var elapsedMs = frame.TimeMs - previousTime;
                    if (double.IsFinite(elapsedMs) && elapsedMs > 0)
                    {
                        positiveElapsedMs += elapsedMs;
                        positiveTimeIntervals++;
                    }
                    else if (elapsedMs < 0)
                    {
                        recentTimesMs.Clear();
                    }
                }
                previousTimeMs = frame.TimeMs;
                if (double.IsFinite(frame.TimeMs))
                {
                    recentTimesMs.Enqueue(frame.TimeMs);
                    while (recentTimesMs.TryPeek(out var timestamp) && frame.TimeMs - timestamp > 2000)
                    {
                        recentTimesMs.Dequeue();
                    }
                }
                retained.Add(frame);
                if (retained.Count > MaximumAnalysisFrames) retained.RemoveRange(0, TrimChunk);
                total++;
            }
        }
        if (total == 0) throw new InvalidDataException("未在BIN中识别到有效JustFloat帧。");
        var sequence = continuity.GetSnapshot();
        var averageFrameRate = positiveElapsedMs <= 0 ? 0 : positiveTimeIntervals * 1000.0 / positiveElapsedMs;
        var recentFrameRate = recentTimesMs.Count < 2
            ? 0
            : (recentTimesMs.Count - 1) * 1000.0 / Math.Max(.001, recentTimesMs.Last() - recentTimesMs.Peek());
        return new BinaryLoadResult(
            retained, total, sequence.LostFrames, sequence.GapEvents,
            sequence.Anomalies, sequence.Restarts, sequence.NominalStep ?? 0,
            sequence.IsStepConfirmed, sequence.LastSequence,
            recentFrameRate, averageFrameRate, parser.FloatCount);
    }

    private async Task LoadTemplateAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择信号Excel模板",
            Filter = "Excel模板 (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
        };
        if (dialog.ShowDialog() != true) return;
        await LoadTemplateFileAsync(dialog.FileName);
    }

    public async Task LoadTemplateFileAsync(string path)
    {
        try
        {
            var definitions = await Task.Run(() => SignalTemplateService.Load(path));
            _template.Clear();
            foreach (var definition in definitions) _template[definition.Index] = definition;
            var count = Math.Max(Fields.Count, definitions.Max(item => item.Index) + 1);
            BuildFieldCatalog(count, useDefaults: false, applyTemplateLayout: true);
            TemplateStatusText = $"{Path.GetFileName(path)} · {definitions.Count}项";
            AppendLog($"已加载Excel信号模板：{path}");
        }
        catch (Exception ex)
        {
            TemplateStatusText = $"模板错误：{ex.Message}";
            AppendLog($"Excel模板加载失败：{ex.Message}");
        }
    }

    private void OnHssSampleReceived(HssSample sample)
    {
        var values = new float[_hssVariables.Count + 2];
        values[0] = (float)sample.Index;
        values[1] = sample.TimestampUs / 1000f;
        for (var i = 0; i < _hssVariables.Count && i < sample.Values.Count; i++) values[i + 2] = (float)sample.Values[i];
        OnFrameReceived(new RttFrame(values, [], sample.ReceivedAt, values.Length));
        _lastHssActivityUtc = DateTime.UtcNow;
    }

    private void ApplyHssState(bool active)
    {
        if (_activeConnectionMode != ConnectionMode.Hss && !active) return;
        if (active)
        {
            ConnectionStatus = "HSS在线";
            StatusBrush = Brush(52, 211, 153);
            return;
        }
        if (_isSessionRunning)
        {
            ConnectionStatus = "HSS已停止";
            StatusBrush = Brush(148, 163, 184);
        }
    }

    private void OnFrameReceived(RttFrame frame)
    {
        lock (_historyGate)
        {
            _latestFrame = frame;
            _analysisFrames.Add(frame);
            if (_analysisFrames.Count > MaximumAnalysisFrames)
            {
                _analysisFrames.RemoveRange(0, Math.Min(TrimChunk, _analysisFrames.Count));
            }
        }
        if (frame.FloatCount != Fields.Count) Interlocked.Exchange(ref _pendingFloatCount, frame.FloatCount);
        AnalysisDataChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record BinaryLoadResult(
        IReadOnlyList<RttFrame> Frames,
        long TotalFrames,
        long LostFrames,
        long GapEvents,
        long SequenceAnomalies,
        long Restarts,
        long SequenceStep,
        bool IsSequenceStepConfirmed,
        long LastSequence,
        double RecentFrameRate,
        double AverageFrameRate,
        int FloatCount);

    private void RefreshDisplay()
    {
        var pending = Interlocked.Exchange(ref _pendingFloatCount, 0);
        if (pending > 0 && pending != Fields.Count)
        {
            lock (_historyGate) _analysisFrames.Clear();
            BuildFieldCatalog(pending, useDefaults: false);
            SchemaText = $"{pending} float · {pending * 4 + 4} bytes/帧";
            AppendLog($"自动识别JustFloat通道数：{pending}");
            AnalysisDataChanged?.Invoke(this, EventArgs.Empty);
        }

        RttFrame? latest;
        lock (_historyGate) latest = _latestFrame;
        if (latest is not null)
        {
            var count = Math.Min(latest.Values.Length, Fields.Count);
            for (var i = 0; i < count; i++) Fields[i].Value = latest.Values[i];
        }

        if (_offlineMode)
        {
            FrameCountText = _offlineFrameCount.ToString("N0");
            FrameRateText = $"{_offlineRecentFrameRate:F1} / {_offlineFrameRate:F1} Hz";
            LostFrameText = $"{_offlineLostFrames:N0} / {_offlineGapEvents:N0}";
            var offlineTotal = _offlineFrameCount + _offlineLostFrames;
            var offlineLossRate = offlineTotal == 0 ? 0 : _offlineLostFrames * 100.0 / offlineTotal;
            var offlineStep = _offlineSequenceStepConfirmed ? $"×{_offlineSequenceStep:N0}" : "学习中";
            LostRateText = $"{offlineLossRate:F3} % / {offlineStep}";
            SequenceHealthText = $"异常 {_offlineSequenceAnomalies:N0} · 重启 {_offlineRestarts:N0}";
            SequenceText = _offlineLastSequence.ToString("N0");
            ThroughputText = $"{_offlineRecentFrameRate * (Fields.Count * 4 + 4) / 1024.0:F1} KiB/s";
            return;
        }
        if (_activeConnectionMode == ConnectionMode.Hss || (!_isSessionRunning && ConnectionMode == ConnectionMode.Hss))
        {
            var hss = (_activeHssSession ?? _hssSession).GetStatistics();
            FrameCountText = hss.ReceivedSamples.ToString("N0");
            FrameRateText = $"{hss.RecentSamplesPerSecond:F1} / {hss.AverageSamplesPerSecond:F1} Hz";
            LostFrameText = "探针定时 / --";
            LostRateText = "HSS / --";
            SequenceHealthText = hss.Capabilities is { } caps
                ? $"能力 {caps.MaxBlocks}变量 · {caps.MaxFrequencyHz:N0}Hz"
                : "等待GetCaps硬件确认";
            SequenceText = hss.ReceivedSamples == 0 ? "-" : (hss.ReceivedSamples - 1).ToString("N0");
            ThroughputText = $"累计 {hss.ReceivedBytes / 1024.0:F1} KiB";
            SchemaText = $"HSS · {_hssVariables.Count}变量 · {HssSampleRateHz}Hz";
            if (!string.IsNullOrEmpty(hss.LastError))
            {
                ConnectionStatus = "HSS异常";
                ConnectionDetail = hss.LastError;
                StatusBrush = Brush(248, 113, 113);
            }
            UpdateDataStatus(DateTime.UtcNow - _lastHssActivityUtc < TimeSpan.FromSeconds(2));
            return;
        }
        var stats = _session.GetStatistics();
        FrameCountText = stats.ReceivedFrames.ToString("N0");
        FrameRateText = $"{stats.RecentFramesPerSecond:F1} / {stats.AverageFramesPerSecond:F1} Hz";
        LostFrameText = $"{stats.LostFrames:N0} / {stats.GapEvents:N0}";
        var total = stats.ReceivedFrames + stats.LostFrames;
        var lossRate = total == 0 ? 0 : stats.LostFrames * 100.0 / total;
        var sequenceStep = stats.IsSequenceStepConfirmed ? $"×{stats.SequenceStep:N0}" : "学习中";
        LostRateText = $"{lossRate:F3} % / {sequenceStep}";
        SequenceHealthText = $"异常 {stats.SequenceAnomalies:N0} · 重启 {stats.TargetRestarts:N0}";
        SequenceText = stats.LastSequence < 0 ? "-" : stats.LastSequence.ToString("N0");
        ThroughputText = $"{stats.RecentBytesPerSecond / 1024.0:F1} KiB/s";
        if (stats.DetectedFloatCount > 0) SchemaText = $"{stats.DetectedFloatCount} float · {stats.DetectedFloatCount * 4 + 4} bytes/帧";
        RecordButtonText = stats.IsRecording ? "停止记录" : "开始记录";
        if (IsDataFlashing && !_session.HasRecentDataActivity) UpdateDataStatus(false);
    }

    private void BuildFieldCatalog(int count, bool useDefaults, bool applyTemplateLayout = false)
    {
        var state = Fields.ToDictionary(field => field.Index, field => (field.IsPlotted, field.PlotGroup));
        foreach (var old in Fields) old.PropertyChanged -= Field_OnPropertyChanged;
        Fields.Clear();
        foreach (var baseDescriptor in RttFieldCatalog.GetAll(count))
        {
            var descriptor = baseDescriptor;
            if (_template.TryGetValue(baseDescriptor.Index, out var definition))
            {
                descriptor = new RttFieldDescriptor(
                    definition.Index,
                    definition.Key,
                    definition.DisplayName,
                    definition.Unit,
                    definition.Group,
                    definition.Format,
                    definition.IntegerLike,
                    definition.Color);
            }
            var field = new FieldValueViewModel(descriptor);
            if (definition is not null && applyTemplateLayout)
            {
                field.IsPlotted = definition.Visible;
                field.PlotGroup = definition.PlotGroup;
            }
            else if (state.TryGetValue(field.Index, out var oldState))
            {
                field.IsPlotted = oldState.IsPlotted;
                field.PlotGroup = oldState.PlotGroup;
            }
            else if (definition is not null)
            {
                field.IsPlotted = definition.Visible;
                field.PlotGroup = definition.PlotGroup;
            }
            field.PropertyChanged += Field_OnPropertyChanged;
            Fields.Add(field);
        }
        RefreshSelectedFields();
        OnPropertyChanged(nameof(Fields));
        SignalLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildHssFieldCatalog()
    {
        foreach (var old in Fields) old.PropertyChanged -= Field_OnPropertyChanged;
        Fields.Clear();
        Add(new RttFieldDescriptor(0, "HSS_SEQ", "HSS样本序号", string.Empty, "HSS基础", "F0", true), false);
        Add(new RttFieldDescriptor(1, "HSS_TIME_MS", "HSS时间", "ms", "HSS基础", "F3"), false);
        for (var i = 0; i < _hssVariables.Count; i++)
        {
            var variable = _hssVariables[i];
            var integer = variable.NumericType is not (ElfNumericType.Float32 or ElfNumericType.Float64);
            var format = integer ? "F0" : "G7";
            Add(new RttFieldDescriptor(i + 2, variable.Name, variable.Symbol.Name, string.Empty, "HSS变量", format, integer), true);
        }
        RefreshSelectedFields();
        OnPropertyChanged(nameof(Fields));
        SignalLayoutChanged?.Invoke(this, EventArgs.Empty);

        void Add(RttFieldDescriptor descriptor, bool plotted)
        {
            var field = new FieldValueViewModel(descriptor) { IsPlotted = plotted, PlotGroup = 1 };
            field.PropertyChanged += Field_OnPropertyChanged;
            Fields.Add(field);
        }
    }

    private void Field_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingSignalLayout || e.PropertyName is not (nameof(FieldValueViewModel.IsPlotted) or nameof(FieldValueViewModel.PlotGroup))) return;
        NotifySignalLayoutChanged();
    }

    private void RefreshSelectedFields()
    {
        _isApplyingSignalLayout = true;
        try
        {
            var selected = Fields.Where(field => field.IsPlotted).ToArray();
            if (SelectedFields.SequenceEqual(selected)) return;
            SelectedFields.Clear();
            foreach (var field in selected) SelectedFields.Add(field);
        }
        finally
        {
            _isApplyingSignalLayout = false;
        }
    }

    private void ClearData()
    {
        lock (_historyGate)
        {
            _analysisFrames.Clear();
            _latestFrame = null;
        }
        Interlocked.Exchange(ref _pendingFloatCount, 0);
        foreach (var field in Fields) field.Value = float.NaN;

        var clearedOfflineData = _offlineMode;
        _offlineMode = false;
        _offlineFrameCount = 0;
        _offlineLostFrames = 0;
        _offlineGapEvents = 0;
        _offlineSequenceAnomalies = 0;
        _offlineRestarts = 0;
        _offlineSequenceStep = 0;
        _offlineSequenceStepConfirmed = false;
        _offlineLastSequence = -1;
        _offlineFrameRate = 0;
        _offlineRecentFrameRate = 0;
        _session.ClearStatistics();
        _hssSession.ClearStatistics();

        FrameCountText = "0";
        FrameRateText = "0.0 Hz";
        LostFrameText = "0";
        LostRateText = "0.000 % / 学习中";
        SequenceHealthText = "异常 0 · 重启 0";
        SequenceText = "-";
        ThroughputText = "0.0 KiB/s";
        if (!_isSessionRunning) SchemaText = "自动检测";
        DataStatusText = _isSessionRunning ? "等待数据" : "无数据";
        DataStatusBrush = Brush(107, 114, 128);
        if (clearedOfflineData)
        {
            ConnectionStatus = "未连接";
            ConnectionDetail = "离线数据已清空";
            StatusBrush = Brush(148, 163, 184);
        }
        AppendLog("已清空内存数据、波形和统计；Excel信号配置与磁盘BIN文件均保留。");
        AnalysisDataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyConnectionState(RttConnectionState state)
    {
        var endpoint = ConnectionMode == ConnectionMode.JLinkRtt ? $"127.0.0.1:{RttPort}" : $"{ServerIp}:{ServerPort}";
        var mode = ConnectionMode == ConnectionMode.JLinkRtt ? "J-Link RTT独立" : "TCP直连";
        (ConnectionStatus, ConnectionDetail, StatusBrush) = state switch
        {
            RttConnectionState.Connecting => ("正在连接", $"{mode} · {endpoint}", Brush(251, 191, 36)),
            RttConnectionState.Connected => ("在线", $"{mode} · {endpoint}", Brush(52, 211, 153)),
            RttConnectionState.Reconnecting => ("正在重连", endpoint, Brush(251, 191, 36)),
            RttConnectionState.Faulted => ("连接异常", "后台将自动重连", Brush(248, 113, 113)),
            _ => ("未连接", "TCP客户端等待连接", Brush(148, 163, 184)),
        };
        if (state == RttConnectionState.Disconnected)
        {
            _isSessionRunning = false;
            RaiseCommandStates();
        }
    }

    private void UpdateDataStatus(bool active)
    {
        IsDataFlashing = active;
        DataStatusBrush = active ? Brush(34, 197, 94) : Brush(107, 114, 128);
        DataStatusText = active ? "数据接收中" : (_isSessionRunning ? "等待数据" : "无数据");
    }

    private void AppendLog(string message)
    {
        _logLines.Enqueue(message);
        while (_logLines.Count > 300) _logLines.Dequeue();
        LogText = string.Join(Environment.NewLine, _logLines);
    }

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        LoadBinaryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsConnected));
    }

    private static string FormatHandshake(string value) => string.IsNullOrEmpty(value) ? "<无>" : value;
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
