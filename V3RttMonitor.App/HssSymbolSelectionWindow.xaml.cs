using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using V3RttMonitor.App.ViewModels;
using V3RttMonitor.Core.Hss;

namespace V3RttMonitor.App;

public partial class HssSymbolSelectionWindow : Window
{
    private readonly ListCollectionView _view;
    public ObservableCollection<HssSymbolItemViewModel> Items { get; }
    public IReadOnlyList<HssVariableSelection> SelectedVariables { get; private set; } = [];

    public HssSymbolSelectionWindow(ElfSymbolCatalog catalog, IReadOnlyList<HssVariableSelection> existing)
    {
        InitializeComponent();
        var selected = existing.ToDictionary(item => item.Symbol.Name, StringComparer.Ordinal);
        Items = new ObservableCollection<HssSymbolItemViewModel>(catalog.Symbols.Select(symbol =>
            new HssSymbolItemViewModel(symbol, selected.GetValueOrDefault(symbol.Name))));
        foreach (var item in Items) item.PropertyChanged += Item_OnPropertyChanged;
        _view = new ListCollectionView(Items) { Filter = FilterItem };
        SymbolGrid.ItemsSource = _view;
        ElfSummaryText.Text = $"{Path.GetFileName(catalog.ElfPath)} · RAM对象 {catalog.Symbols.Count:N0} · 可绘标量 {catalog.Symbols.Count(item => item.IsScalarCandidate):N0}";
        UpdateStatus();
    }

    private bool FilterItem(object value)
    {
        if (value is not HssSymbolItemViewModel item) return false;
        if (ScalarOnlyCheck.IsChecked == true && !item.CanSelect) return false;
        var query = SearchBox.Text.Trim();
        return query.Length == 0
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Section.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_OnChanged(object sender, RoutedEventArgs e) => _view?.Refresh();

    private void SelectVisibleButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (HssSymbolItemViewModel item in _view.Cast<object>().OfType<HssSymbolItemViewModel>().Where(item => item.CanSelect)) item.IsSelected = true;
        UpdateStatus();
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsSelected = false;
        UpdateStatus();
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = Items.Where(item => item.IsSelected && item.CanSelect).Select(item => item.ToSelection()).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "至少选择一个可绘制的标量变量。", "HSS变量", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedVariables = selected;
        DialogResult = true;
    }

    private void Item_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HssSymbolItemViewModel.IsSelected)) UpdateStatus();
    }

    private void UpdateStatus()
    {
        var count = Items.Count(item => item.IsSelected);
        SelectionStatusText.Text = count > 10
            ? $"已选择 {count:N0} 个变量；普通V9探针通常最多10个，真机GetCaps可能拒绝当前配置。"
            : $"已选择 {count:N0} 个变量；连接时将按探针GetCaps限制再次校验。";
        SelectionStatusText.Foreground = new System.Windows.Media.SolidColorBrush(count > 10
            ? System.Windows.Media.Color.FromRgb(251, 191, 36)
            : System.Windows.Media.Color.FromRgb(148, 163, 184));
    }
}
