using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.Core.Protocol;

namespace V3RttMonitor.App.ViewModels;

public sealed class FieldValueViewModel(RttFieldDescriptor descriptor) : ObservableObject
{
    private float _value = float.NaN;
    private bool _isPlotted;
    private int _plotGroup = 1;

    private static readonly string[] Colors =
    ["#38BDF8", "#34D399", "#FBBF24", "#F87171", "#C084FC", "#F472B6", "#2DD4BF", "#FB923C"];

    public RttFieldDescriptor Descriptor { get; } = descriptor;
    public int Index => Descriptor.Index;
    public string Key => Descriptor.Key;
    public string DisplayName => Descriptor.DisplayName;
    public string Unit => Descriptor.Unit;
    public string Group => Descriptor.Group;
    public string ColorHex => Descriptor.ColorHex ?? Colors[Index % Colors.Length];

    public float Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(ValueText));
            }
        }
    }

    public string ValueText => float.IsNaN(Value)
        ? "-"
        : float.IsFinite(Value)
        ? Value.ToString(Descriptor.Format, System.Globalization.CultureInfo.InvariantCulture)
        : Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsPlotted
    {
        get => _isPlotted;
        set => SetProperty(ref _isPlotted, value);
    }

    public int PlotGroup
    {
        get => _plotGroup;
        set => SetProperty(ref _plotGroup, Math.Max(1, value));
    }
}
