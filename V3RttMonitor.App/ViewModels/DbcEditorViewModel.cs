using System.Collections.ObjectModel;
using System.Globalization;
using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App.ViewModels;

public sealed class DbcEditorViewModel : ObservableObject
{
    private DbcMessageEditorItem? _selectedMessage;
    private string _databaseName = "CAN_Database";

    public DbcEditorViewModel(DbcDatabase? source, CanMessageItemViewModel? selectedCanMessage)
    {
        Messages = [];
        if (source is not null)
        {
            DatabaseName = source.Name;
            foreach (var message in source.Messages) Messages.Add(new DbcMessageEditorItem(message));
        }
        if (Messages.Count == 0)
        {
            var message = new DbcMessageEditorItem
            {
                Id = selectedCanMessage?.Id ?? 0x100,
                IsExtended = selectedCanMessage?.IsExtended ?? false,
                Name = selectedCanMessage?.Name is { Length: > 0 } name && name != "未定义报文" ? name : "NewMessage",
                Length = Math.Clamp(selectedCanMessage?.PayloadLength ?? 8, 1, 64),
            };
            Messages.Add(message);
        }
        SelectedMessage = selectedCanMessage is null
            ? Messages.FirstOrDefault()
            : Messages.FirstOrDefault(item => item.Id == selectedCanMessage.Id && item.IsExtended == selectedCanMessage.IsExtended) ?? Messages.FirstOrDefault();
    }

    public ObservableCollection<DbcMessageEditorItem> Messages { get; }
    public string DatabaseName { get => _databaseName; set => SetProperty(ref _databaseName, value); }
    public DbcMessageEditorItem? SelectedMessage { get => _selectedMessage; set => SetProperty(ref _selectedMessage, value); }

    public DbcDatabase ToDatabase()
    {
        var database = new DbcDatabase { Name = DbcParser.SanitizeName(DatabaseName) };
        foreach (var item in Messages) database.Messages.Add(item.ToModel());
        return database;
    }
}

public sealed class DbcMessageEditorItem : ObservableObject
{
    private uint _id;
    private bool _isExtended;
    private string _name = "NewMessage";
    private int _length = 8;
    private string _sender = "Vector__XXX";
    private int? _cycleTimeMs;
    private string _comment = string.Empty;
    private DbcSignalEditorItem? _selectedSignal;

    public DbcMessageEditorItem()
    {
        Signals = [];
    }

    public DbcMessageEditorItem(DbcMessage source) : this()
    {
        Id = source.Id;
        IsExtended = source.IsExtended;
        Name = source.Name;
        Length = source.Length;
        Sender = source.Sender;
        CycleTimeMs = source.CycleTimeMs;
        Comment = source.Comment;
        foreach (var signal in source.Signals) Signals.Add(new DbcSignalEditorItem(signal));
        SelectedSignal = Signals.FirstOrDefault();
    }

    public ObservableCollection<DbcSignalEditorItem> Signals { get; }
    public uint Id { get => _id; set { if (SetProperty(ref _id, value)) { OnPropertyChanged(nameof(IdText)); OnPropertyChanged(nameof(DisplayText)); } } }
    public string IdText
    {
        get => IsExtended ? $"0x{Id:X8}" : $"0x{Id:X3}";
        set
        {
            var text = value.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('x', 'X');
            if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) Id = parsed & 0x1FFFFFFF;
        }
    }
    public bool IsExtended { get => _isExtended; set { if (SetProperty(ref _isExtended, value)) { OnPropertyChanged(nameof(IdText)); OnPropertyChanged(nameof(DisplayText)); } } }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(DisplayText)); } }
    public int Length { get => _length; set => SetProperty(ref _length, Math.Clamp(value, 1, 64)); }
    public string Sender { get => _sender; set => SetProperty(ref _sender, value); }
    public int? CycleTimeMs { get => _cycleTimeMs; set => SetProperty(ref _cycleTimeMs, value); }
    public string Comment { get => _comment; set => SetProperty(ref _comment, value); }
    public string DisplayText => $"{IdText}  {Name}";
    public DbcSignalEditorItem? SelectedSignal { get => _selectedSignal; set => SetProperty(ref _selectedSignal, value); }

    public DbcMessage ToModel()
    {
        var message = new DbcMessage { Id = Id, IsExtended = IsExtended, Name = DbcParser.SanitizeName(Name), Length = Length, Sender = DbcParser.SanitizeName(Sender), CycleTimeMs = CycleTimeMs, Comment = Comment };
        foreach (var signal in Signals) message.Signals.Add(signal.ToModel());
        return message;
    }
}

public sealed class DbcSignalEditorItem : ObservableObject
{
    private string _name = "NewSignal";
    private int _startBit;
    private int _length = 8;
    private DbcByteOrder _byteOrder = DbcByteOrder.Intel;
    private bool _isSigned;
    private double _factor = 1;
    private double _offset;
    private double _minimum;
    private double _maximum = 255;
    private string _unit = string.Empty;
    private bool _isMultiplexer;
    private int? _multiplexerValue;
    private string _receiver = "Vector__XXX";
    private string _comment = string.Empty;
    private DbcSignalValueType _valueType;

    public DbcSignalEditorItem() { }
    public DbcSignalEditorItem(DbcSignal source)
    {
        Name = source.Name;
        StartBit = source.StartBit;
        Length = source.Length;
        ByteOrder = source.ByteOrder;
        IsSigned = source.IsSigned;
        Factor = source.Factor;
        Offset = source.Offset;
        Minimum = source.Minimum;
        Maximum = source.Maximum;
        Unit = source.Unit;
        IsMultiplexer = source.IsMultiplexer;
        MultiplexerValue = source.MultiplexerValue;
        Receiver = source.Receiver;
        Comment = source.Comment;
        ValueType = source.ValueType;
        foreach (var choice in source.Choices) Choices[choice.Key] = choice.Value;
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public IReadOnlyList<DbcByteOrder> ByteOrderOptions { get; } = Enum.GetValues<DbcByteOrder>();
    public int StartBit { get => _startBit; set => SetProperty(ref _startBit, Math.Max(0, value)); }
    public int Length { get => _length; set => SetProperty(ref _length, Math.Clamp(value, 1, 64)); }
    public DbcByteOrder ByteOrder { get => _byteOrder; set => SetProperty(ref _byteOrder, value); }
    public bool IsSigned { get => _isSigned; set => SetProperty(ref _isSigned, value); }
    public double Factor { get => _factor; set => SetProperty(ref _factor, value); }
    public double Offset { get => _offset; set => SetProperty(ref _offset, value); }
    public double Minimum { get => _minimum; set => SetProperty(ref _minimum, value); }
    public double Maximum { get => _maximum; set => SetProperty(ref _maximum, value); }
    public string Unit { get => _unit; set => SetProperty(ref _unit, value); }
    public bool IsMultiplexer { get => _isMultiplexer; set => SetProperty(ref _isMultiplexer, value); }
    public int? MultiplexerValue { get => _multiplexerValue; set => SetProperty(ref _multiplexerValue, value); }
    public string Receiver { get => _receiver; set => SetProperty(ref _receiver, value); }
    public string Comment { get => _comment; set => SetProperty(ref _comment, value); }
    public DbcSignalValueType ValueType { get => _valueType; set => SetProperty(ref _valueType, value); }
    public Dictionary<long, string> Choices { get; } = [];

    public DbcSignal ToModel()
    {
        var signal = new DbcSignal
        {
            Name = DbcParser.SanitizeName(Name),
            StartBit = StartBit,
            Length = Length,
            ByteOrder = ByteOrder,
            IsSigned = IsSigned,
            Factor = Factor,
            Offset = Offset,
            Minimum = Minimum,
            Maximum = Maximum,
            Unit = Unit,
            IsMultiplexer = IsMultiplexer,
            MultiplexerValue = MultiplexerValue,
            Receiver = Receiver,
            Comment = Comment,
            ValueType = ValueType,
        };
        foreach (var choice in Choices) signal.Choices[choice.Key] = choice.Value;
        return signal;
    }
}
