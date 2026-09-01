using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.Core.Hss;

namespace V3RttMonitor.App.ViewModels;

public sealed class HssSymbolItemViewModel : ObservableObject
{
    private bool _isSelected;
    private ElfNumericType _numericType;

    public HssSymbolItemViewModel(ElfSymbol symbol, HssVariableSelection? existing = null)
    {
        Symbol = symbol;
        _numericType = existing?.NumericType ?? symbol.DefaultNumericType;
        _isSelected = existing is not null;
    }

    public ElfSymbol Symbol { get; }
    public string Name => Symbol.Name;
    public string Address => Symbol.AddressText;
    public string SizeText => $"{Symbol.Size} B";
    public string Section => Symbol.SectionName;
    public string Scope => Symbol.Binding.ToString();
    public IReadOnlyList<ElfNumericType> NumericTypes => Symbol.NumericTypes;
    public bool CanSelect => Symbol.IsScalarCandidate && Symbol.IsHssAddressSupported;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value && CanSelect);
    }

    public ElfNumericType NumericType
    {
        get => _numericType;
        set => SetProperty(ref _numericType, value);
    }

    public HssVariableSelection ToSelection() => new()
    {
        Symbol = Symbol,
        NumericType = NumericType,
    };
}
