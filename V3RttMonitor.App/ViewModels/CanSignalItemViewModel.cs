using System.Globalization;
using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App.ViewModels;

public sealed class CanSignalItemViewModel(DbcMessage message, DbcSignal signal, int colorIndex, bool isRawByte = false) : ObservableObject
{
    private static readonly string[] Colors = ["#38BDF8", "#34D399", "#FBBF24", "#F87171", "#C084FC", "#F472B6", "#2DD4BF", "#FB923C"];
    private bool _isPlotted;
    private bool _isFavorite;
    private double _value = double.NaN;
    private string? _choiceText;
    private int _plotGroup = 1;
    private string _frequencyText = "-";

    public DbcMessage Message { get; } = message;
    public DbcSignal Signal { get; } = signal;
    public bool IsRawByte { get; } = isRawByte;
    public string StableKey => $"{Message.Id:X8}:{Message.IsExtended}:{Signal.Name}";
    public string Name => Signal.Name;
    public string MessageName => Message.Name;
    public string IdText => Message.Key.ToString();
    public string Unit => Signal.Unit;
    public string DefinitionText => IsRawByte ? $"Byte {Signal.StartBit / 8}" : Signal.DefinitionText;
    public string ByteOrderText => Signal.ByteOrder == DbcByteOrder.Intel ? "Intel" : "Motorola";
    public string ColorHex { get; } = Colors[colorIndex % Colors.Length];
    public bool IsPlotted { get => _isPlotted; set => SetProperty(ref _isPlotted, value); }
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }
    public double Value { get => _value; set { if (SetProperty(ref _value, value)) OnPropertyChanged(nameof(ValueText)); } }
    public string? ChoiceText { get => _choiceText; set { if (SetProperty(ref _choiceText, value)) OnPropertyChanged(nameof(ValueText)); } }
    public string ValueText => !double.IsFinite(Value) ? "-" : ChoiceText is null ? Value.ToString("G9", CultureInfo.InvariantCulture) : $"{Value:G9} ({ChoiceText})";
    public int PlotGroup { get => _plotGroup; set => SetProperty(ref _plotGroup, Math.Clamp(value, 1, 4)); }
    public string FrequencyText { get => _frequencyText; set => SetProperty(ref _frequencyText, value); }
}

public sealed class CanFrameRowViewModel
{
    public CanFrameRowViewModel(CanFrame frame, string messageName)
    {
        Frame = frame;
        MessageName = messageName;
    }

    public CanFrame Frame { get; }
    public string TimeText => Frame.TimestampSeconds.ToString("F6", CultureInfo.InvariantCulture);
    public int Channel => Frame.Channel;
    public string DirectionText => Frame.Direction.ToString();
    public string IdText => Frame.IdText;
    public string MessageName { get; }
    public string TypeText => Frame.Kind == CanFrameKind.Error ? "Error" : Frame.Kind == CanFrameKind.Remote ? "Remote" : Frame.IsFd ? "CAN FD" : "CAN";
    public int Dlc => Frame.Dlc;
    public string DataText => Frame.DataText;
}

public sealed class DbcFileItemViewModel(string name, string path, int messageCount, int signalCount)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public int MessageCount { get; } = messageCount;
    public int SignalCount { get; } = signalCount;
    public string DisplayText => $"{Name} · {MessageCount}报文 · {SignalCount}信号";
}
