using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App.ViewModels;

public sealed class CanMessageItemViewModel(CanFrameKey key) : ObservableObject
{
    private string _name = "未定义报文";
    private long _count;
    private double _firstTimestamp;
    private double _lastTimestamp;
    private int _payloadLength;
    private string _lastData = string.Empty;
    private CanDirection _direction;
    private bool _isFd;
    private bool _isFavorite;
    private bool _isDbcDefined;

    public CanFrameKey Key { get; } = key;
    public uint Id => Key.Id;
    public bool IsExtended => Key.IsExtended;
    public string IdText => Key.ToString();
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public long Count { get => _count; private set { if (SetProperty(ref _count, value)) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(HasReceived)); OnPropertyChanged(nameof(CatalogStateText)); } } }
    public string CountText => Count.ToString("N0");
    public double FirstTimestamp { get => _firstTimestamp; private set => SetProperty(ref _firstTimestamp, value); }
    public double LastTimestamp { get => _lastTimestamp; private set { if (SetProperty(ref _lastTimestamp, value)) { OnPropertyChanged(nameof(RateText)); OnPropertyChanged(nameof(LastTimeText)); } } }
    public string LastTimeText => $"{LastTimestamp:F6}s";
    public int PayloadLength { get => _payloadLength; private set => SetProperty(ref _payloadLength, value); }
    public string LastData { get => _lastData; private set => SetProperty(ref _lastData, value); }
    public CanDirection Direction { get => _direction; private set => SetProperty(ref _direction, value); }
    public bool IsFd { get => _isFd; private set => SetProperty(ref _isFd, value); }
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }
    public bool IsDbcDefined { get => _isDbcDefined; set { if (SetProperty(ref _isDbcDefined, value)) OnPropertyChanged(nameof(CatalogStateText)); } }
    public bool HasReceived => Count > 0;
    public string CatalogStateText => HasReceived ? (IsDbcDefined ? "已接收 · DBC" : "已接收 · 未定义") : "DBC定义 · 未接收";
    public string RateText => Count < 2 || LastTimestamp <= FirstTimestamp ? "-" : $"{(Count - 1) / (LastTimestamp - FirstTimestamp):F1} Hz";
    public string FrameTypeText => IsFd ? "CAN FD" : IsExtended ? "扩展帧" : "标准帧";

    public void Observe(CanFrame frame)
    {
        if (Count == 0) FirstTimestamp = frame.TimestampSeconds;
        Count++;
        LastTimestamp = frame.TimestampSeconds;
        PayloadLength = Math.Max(PayloadLength, frame.Data.Length);
        LastData = frame.DataText;
        Direction = frame.Direction;
        IsFd |= frame.IsFd;
        OnPropertyChanged(nameof(RateText));
    }

    public void ResetStatistics()
    {
        Count = 0;
        FirstTimestamp = 0;
        LastTimestamp = 0;
        PayloadLength = 0;
        LastData = string.Empty;
        Direction = CanDirection.Unknown;
        IsFd = false;
        OnPropertyChanged(nameof(RateText));
    }
}
