using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.Interactivity.UserActionResponses;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using V3RttMonitor.App.ViewModels;
using V3RttMonitor.Core.CanBus;
using V3RttMonitor.Core.Visualization;

namespace V3RttMonitor.App.Views;

public partial class CanAnalysisView : UserControl
{
    private const int MaximumSignals = 8;
    private const int MaximumRenderedPoints = 50_000;
    private const int MaximumSeriesPoints = 200_000;
    private readonly DispatcherTimer _renderTimer;
    private readonly Dictionary<string, SeriesCache> _series = [];
    private readonly Dictionary<string, FrozenSeries> _pausedSeries = [];
    private readonly Dictionary<int, CanPlotPane> _panes = [];
    private readonly Dictionary<string, FrozenSeries> _renderedSeries = [];
    private readonly Dictionary<string, Marker> _markers = [];
    private CanAnalysisViewModel? _viewModel;
    private ListCollectionView? _messageView;
    private ListCollectionView? _signalView;
    private ListCollectionView? _rawView;
    private Window? _hostWindow;
    private VerticalLine? _cursorLine;
    private bool _subscribed;
    private bool _dataDirty = true;
    private bool _hasRendered;
    private bool _displayPaused;
    private CanPlotInteractionMode _interactionMode = CanPlotInteractionMode.Pointer;
    private bool _sampleMode;
    private bool _cursorLocked;
    private double? _cursorTime;
    private int _cursorGroup = 1;
    private int _activeGroup = 1;
    private bool _dualCursorEnabled;
    private double? _secondCursorTime;
    private CanSampleCursor _activeSampleCursor = CanSampleCursor.A;
    private bool _activePlotMaximized;
    private GridLength _catalogWidthBeforeFocus = new(400);

    public CanAnalysisView()
    {
        InitializeComponent();
        PlotGroupOptions = [1];
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as CanAnalysisViewModel);
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _renderTimer.Tick += (_, _) => RenderIfRequired();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as CanAnalysisViewModel);
        Subscribe();
        BuildCollectionViews();
        BuildPlotPanes();
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown += HostWindow_OnPreviewKeyDown;
        _renderTimer.Start();
        RebuildAllSeries();
        RenderIfRequired();
        if (_viewModel is not null && _viewModel.SelectedSignals.Count == 0 && FramesViewModeButton is not null)
            FramesViewModeButton.IsChecked = true;
        if (_viewModel is not null && _viewModel.HardwareEndpoints.Count == 0)
            await _viewModel.DiscoverHardwareAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer.Stop();
        Unsubscribe();
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown -= HostWindow_OnPreviewKeyDown;
        _hostWindow = null;
    }

    public ObservableCollection<int> PlotGroupOptions { get; }

    private void AttachViewModel(CanAnalysisViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) { Subscribe(); return; }
        Unsubscribe();
        _viewModel = viewModel;
        Subscribe();
        if (IsLoaded) BuildCollectionViews();
    }

    private void Subscribe()
    {
        if (_subscribed || _viewModel is null || !IsLoaded) return;
        _viewModel.FramesChanged += ViewModel_OnFramesChanged;
        _viewModel.SignalSelectionChanged += ViewModel_OnSignalSelectionChanged;
        _viewModel.CatalogChanged += ViewModel_OnCatalogChanged;
        _viewModel.Messages.CollectionChanged += Collection_OnChanged;
        _viewModel.Signals.CollectionChanged += Collection_OnChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _viewModel is null) return;
        _viewModel.FramesChanged -= ViewModel_OnFramesChanged;
        _viewModel.SignalSelectionChanged -= ViewModel_OnSignalSelectionChanged;
        _viewModel.CatalogChanged -= ViewModel_OnCatalogChanged;
        _viewModel.Messages.CollectionChanged -= Collection_OnChanged;
        _viewModel.Signals.CollectionChanged -= Collection_OnChanged;
        _subscribed = false;
    }

    private void BuildCollectionViews()
    {
        if (_viewModel is null) return;
        _messageView = new ListCollectionView(_viewModel.Messages) { Filter = FilterMessage };
        _messageView.SortDescriptions.Add(new SortDescription(nameof(CanMessageItemViewModel.IsFavorite), ListSortDirection.Descending));
        _messageView.SortDescriptions.Add(new SortDescription(nameof(CanMessageItemViewModel.Id), ListSortDirection.Ascending));
        MessageList.ItemsSource = _messageView;
        _signalView = new ListCollectionView(_viewModel.Signals) { Filter = FilterSignal };
        _signalView.SortDescriptions.Add(new SortDescription(nameof(CanSignalItemViewModel.IsFavorite), ListSortDirection.Descending));
        SignalList.ItemsSource = _signalView;
        _rawView = new ListCollectionView(_viewModel.RecentFrames) { Filter = FilterRawFrame };
        RawFrameGrid.ItemsSource = _rawView;
    }

    private bool FilterMessage(object value)
    {
        if (_viewModel is null || value is not CanMessageItemViewModel message) return false;
        if (_viewModel.FavoriteOnly && !message.IsFavorite) return false;
        var scope = (MessageScopeCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (scope == "Received" && !message.HasReceived) return false;
        if (scope == "Dbc" && !message.IsDbcDefined) return false;
        if (scope == "Undefined" && (!message.HasReceived || message.IsDbcDefined)) return false;
        var search = _viewModel.SearchText.Trim();
        return search.Length == 0
            || message.IdText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || message.Id.ToString("X", CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase)
            || message.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || message.LastData.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (_viewModel.Database?.FindMessage(message.Id, message.IsExtended)?.Signals.Any(signal =>
                signal.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || signal.Unit.Contains(search, StringComparison.OrdinalIgnoreCase)) == true);
    }

    private bool FilterSignal(object value)
    {
        if (_viewModel is null || value is not CanSignalItemViewModel signal) return false;
        if (_viewModel.FavoriteOnly && !signal.IsFavorite) return false;
        var search = _viewModel.SearchText.Trim();
        return search.Length == 0
            || signal.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || signal.IdText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || signal.MessageName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || signal.Unit.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterRawFrame(object value)
    {
        if (_viewModel is null || value is not CanFrameRowViewModel row) return false;
        if (OnlySelectedMessageCheck.IsChecked == true && _viewModel.SelectedMessage is { } selected && row.Frame.Key != selected.Key) return false;
        var search = _viewModel.SearchText.Trim();
        return search.Length == 0
            || row.IdText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.MessageName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.DataText.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => RefreshFilters();
    private void FilterOption_OnChanged(object sender, RoutedEventArgs e) => RefreshFilters();
    private void RawFilter_OnChanged(object sender, RoutedEventArgs e) => _rawView?.Refresh();
    private void Favorite_OnClick(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(RefreshFilters);

    private void RefreshFilters()
    {
        _messageView?.Refresh();
        _signalView?.Refresh();
        _rawView?.Refresh();
    }

    private void Collection_OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshFilters);

    private void ViewModel_OnFramesChanged(object? sender, CanFramesChangedEventArgs e)
    {
        if (e.IsReset) _series.Clear();
        ProcessFrames(e.Frames);
        if (!_displayPaused) _dataDirty = true;
        _rawView?.Refresh();
        if (AutoFollowCheck.IsChecked == true && RecentFramesLast() is { } last) RawFrameGrid.ScrollIntoView(last);
        if (_viewModel is not null && e.Frames.Count > 0 && _viewModel.SelectedSignals.Count == 0)
        {
            SelectionStatusText.Text = $"已收到 {_viewModel.HistoryFrameCount:N0} 帧 · 先选择上方报文ID，再勾选下方信号即可绘图（无DBC时可选原始Byte）。";
        }
    }

    private object? RecentFramesLast() => _viewModel?.RecentFrames.Count > 0 ? _viewModel.RecentFrames[^1] : null;

    private void ViewModel_OnSignalSelectionChanged(object? sender, EventArgs e)
    {
        var requiredGroups = Math.Clamp(_viewModel?.SelectedSignals.Select(signal => signal.PlotGroup).DefaultIfEmpty(1).Max() ?? 1, 1, 4);
        var panesChanged = false;
        while (PlotGroupOptions.Count < requiredGroups)
        {
            PlotGroupOptions.Add(PlotGroupOptions.Count + 1);
            panesChanged = true;
        }
        if (panesChanged) BuildPlotPanes();
        RebuildAllSeries();
        SelectionStatusText.Text = $"已选择 {_viewModel?.SelectedSignals.Count ?? 0} / {MaximumSignals} 个绘图信号 · {PlotGroupOptions.Count}个坐标图。";
    }

    private void ViewModel_OnCatalogChanged(object? sender, EventArgs e) => RefreshFilters();

    private async void LoadDbcButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new OpenFileDialog { Title = "加载一个或多个CAN DBC", Filter = "CAN数据库 (*.dbc)|*.dbc|所有文件 (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (Window.GetWindow(this) is CanAnalysisWindow window) await window.Workspace.LoadDbcFilesAsync(dialog.FileNames);
        else await _viewModel.LoadDbcFilesAsync(dialog.FileNames);
    }

    private void RemoveDbcButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedDbcFile is not { } selected) return;
        if (Window.GetWindow(this) is CanAnalysisWindow window) window.Workspace.RemoveDbc(selected.Path);
        else _viewModel.RemoveSelectedDbc();
    }

    private async void LoadLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "加载一个或多个CAN日志",
            Filter = "CAN日志 (*.blf;*.asc;*.log;*.txt;*.csv)|*.blf;*.asc;*.log;*.txt;*.csv|Vector BLF (*.blf)|*.blf|Vector ASC (*.asc)|*.asc|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        var owner = Window.GetWindow(this);
        if (dialog.ShowDialog(owner) != true) return;
        var mode = CanLogMergeMode.Replace;
        var gap = 0d;
        if (_viewModel.HistoryFrameCount > 0 || dialog.FileNames.Length > 1)
        {
            var mergeDialog = new CanLogMergeWindow(_viewModel.HistoryFrameCount, dialog.FileNames.Length) { Owner = owner };
            if (mergeDialog.ShowDialog() != true) return;
            mode = mergeDialog.MergeMode;
            gap = mergeDialog.GapSeconds;
        }
        await _viewModel.LoadLogsAsync(dialog.FileNames, mode, gap);
        if (_viewModel.SelectedSignals.Count == 0 && FramesViewModeButton is not null)
            FramesViewModeButton.IsChecked = true;
    }

    private void ClearDataButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearFrames();
        var online = _viewModel?.IsOnlineWorkspace == true;
        SelectionStatusText.Text = online
            ? "接收缓存、波形和统计已清空；硬件连接与DBC保留，后续报文继续进入。"
            : "当前离线数据集已关闭；DBC配置保留。";
        PlotStatusText.Text = online ? "等待新的在线CAN报文。" : "等待加载BLF、ASC、LOG或CSV数据。";
    }

    private void ClearSignalsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearSignalSelection();
        SelectionStatusText.Text = "已取消全部绘图信号；报文数据和DBC未删除。";
    }

    private void CleanupMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 2;
        menu.IsOpen = true;
    }

    private async void RefreshHardwareButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.DiscoverHardwareAsync();
    }

    private async void StartTcpButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.StartOnlineAsync();
        if (_viewModel.IsOnline && _viewModel.SelectedSignals.Count == 0 && FramesViewModeButton is not null)
            FramesViewModeButton.IsChecked = true;
    }
    private async void StopTcpButton_OnClick(object sender, RoutedEventArgs e) { if (_viewModel is not null) await _viewModel.StopOnlineAsync(); }

    private void MessageList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _signalView = _viewModel is null ? null : new ListCollectionView(_viewModel.Signals) { Filter = FilterSignal };
        if (_signalView is not null)
        {
            _signalView.SortDescriptions.Add(new SortDescription(nameof(CanSignalItemViewModel.IsFavorite), ListSortDirection.Descending));
            SignalList.ItemsSource = _signalView;
        }
        _rawView?.Refresh();
    }

    private void SignalCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not CheckBox { DataContext: CanSignalItemViewModel signal }) return;
        var selectedCount = _viewModel.SelectedSignals.Count + (signal.IsPlotted && !_viewModel.SelectedSignals.Contains(signal) ? 1 : 0);
        if (signal.IsPlotted && selectedCount > MaximumSignals)
        {
            signal.IsPlotted = false;
            SelectionStatusText.Text = $"最多同时绘制 {MaximumSignals} 个信号。";
            return;
        }
        _viewModel.NotifySignalSelectionChanged();
    }

    private void RebuildAllSeries()
    {
        _series.Clear();
        if (_viewModel is not null) ProcessFrames(_viewModel.GetFramesSnapshot());
        _dataDirty = true;
    }

    private void ProcessFrames(IReadOnlyList<CanFrame> frames)
    {
        if (_viewModel is null || frames.Count == 0 || _viewModel.SelectedSignals.Count == 0) return;
        var selectedByMessage = _viewModel.SelectedSignals.GroupBy(signal => signal.Message.Key).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var frame in frames)
        {
            if (!selectedByMessage.TryGetValue(frame.Key, out var signals)) continue;
            foreach (var signal in signals)
            {
                if (!DbcCodec.TryDecode(signal.Message, signal.Signal, frame.Data, out var decoded)) continue;
                if (!_series.TryGetValue(signal.StableKey, out var series)) _series[signal.StableKey] = series = new SeriesCache(signal);
                series.Xs.Add(frame.TimestampSeconds);
                series.Ys.Add(decoded.PhysicalValue);
                series.SegmentIds.Add(frame.SegmentIndex);
                if (series.Xs.Count > MaximumSeriesPoints)
                {
                    var remove = Math.Min(10_000, series.Xs.Count - MaximumSeriesPoints);
                    series.Xs.RemoveRange(0, remove);
                    series.Ys.RemoveRange(0, remove);
                    series.SegmentIds.RemoveRange(0, remove);
                }
            }
        }
    }

    private void BuildPlotPanes()
    {
        if (PlotPanel is null) return;
        PlotPanel.Children.Clear();
        _panes.Clear();
        WpfPlot? firstPlot = null;
        foreach (var group in PlotGroupOptions)
        {
            var border = new Border
            {
                BorderBrush = GroupBrush(group == _activeGroup),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 0, 7),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var title = new TextBlock { Text = $"图 {group} · 0路", FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 2, 0, 3) };
            var control = new WpfPlot { Focusable = true };
            Grid.SetRow(control, 1);
            var tipText = new TextBlock { FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 11 };
            var tip = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(238, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(70, 12, 12, 12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Child = tipText,
            };
            Grid.SetRow(tip, 1);
            Panel.SetZIndex(tip, 10);
            grid.Children.Add(title);
            grid.Children.Add(control);
            grid.Children.Add(tip);
            border.Child = grid;
            border.Visibility = !_activePlotMaximized || group == _activeGroup ? Visibility.Visible : Visibility.Collapsed;
            PlotPanel.Children.Add(border);
            var pane = new CanPlotPane(group, border, control, title, tip, tipText);
            _panes[group] = pane;

            control.PreviewMouseDown += (_, _) => SetActiveGroup(group);
            control.MouseMove += MultiPlot_OnMouseMove;
            control.PreviewMouseLeftButtonDown += MultiPlot_OnPreviewMouseLeftButtonDown;
            control.PreviewMouseWheel += MultiPlot_OnPreviewMouseWheel;
            control.PreviewMouseRightButtonDown += MultiPlot_OnPreviewMouseRightButtonDown;
            control.PreviewMouseRightButtonUp += MultiPlot_OnPreviewMouseRightButtonUp;
            ConfigureInteraction(control);
            if (firstPlot is null) firstPlot = control;
            else firstPlot.Plot.Axes.Link(control, true, false);
        }
        UpdatePaneHeights();
        UpdateActiveBorders();
        _dataDirty = true;
    }

    private void RenderMultiPlots()
    {
        if (_viewModel is null || _panes.Count == 0) return;
        var source = _displayPaused
            ? _pausedSeries
            : _series.ToDictionary(item => item.Key, item => new FrozenSeries(item.Value.Signal, item.Value.Xs.ToArray(), item.Value.Ys.ToArray(), item.Value.SegmentIds.ToArray()));
        var latest = source.Values.SelectMany(series => series.Xs.TakeLast(1)).DefaultIfEmpty(0).Max();
        var windowSeconds = ParseWindowSeconds();

        foreach (var pane in _panes.Values)
        {
            var oldLimits = pane.HasRendered ? pane.Control.Plot.Axes.GetLimits() : default;
            var plot = pane.Control.Plot;
            plot.Clear();
            StylePlot(plot);
            pane.Markers.Clear();
            pane.SecondMarkers.Clear();
            pane.Rendered.Clear();
            var selectedSignals = _viewModel.SelectedSignals.Where(signal => signal.PlotGroup == pane.Group).ToArray();
            pane.Title.Text = $"图 {pane.Group} · {selectedSignals.Length}路";
            foreach (var selected in selectedSignals)
            {
                if (!source.TryGetValue(selected.StableKey, out var full) || full.Xs.Length == 0) continue;
                var start = windowSeconds <= 0 ? 0 : Array.FindIndex(full.Xs, value => value >= latest - windowSeconds);
                if (start < 0) start = Math.Max(0, full.Xs.Length - 1);
                var xs = full.Xs[start..];
                var ys = full.Ys[start..];
                var segments = full.SegmentIds.Length == full.Xs.Length ? full.SegmentIds[start..] : new int[xs.Length];
                (xs, ys, segments) = Downsample(xs, ys, segments);
                var rendered = new FrozenSeries(selected, xs, ys, segments);
                pane.Rendered[selected.StableKey] = rendered;
                var (plotXs, plotYs) = InsertSegmentBreaks(xs, ys, segments);
                var scatter = plot.Add.Scatter(plotXs, plotYs);
                scatter.Color = ScottPlot.Color.FromHex(selected.ColorHex);
                scatter.LineWidth = 1.5f;
                scatter.MarkerSize = ShowPointsCheck.IsChecked == true ? 4 : 0;
                var latestValue = ys[^1].ToString("G7", CultureInfo.InvariantCulture);
                scatter.LegendText = string.IsNullOrWhiteSpace(selected.Unit) ? $"{selected.Name} = {latestValue}" : $"{selected.Name} = {latestValue} {selected.Unit}";
                var marker = plot.Add.Marker(0, 0);
                marker.Color = scatter.Color;
                marker.MarkerShape = _interactionMode == CanPlotInteractionMode.Sample ? MarkerShape.FilledCircle : MarkerShape.OpenCircle;
                marker.MarkerSize = _interactionMode == CanPlotInteractionMode.Sample ? 13 : 9;
                marker.IsVisible = false;
                pane.Markers[selected.StableKey] = marker;
                var secondMarker = plot.Add.Marker(0, 0);
                secondMarker.Color = ScottPlot.Color.FromHex("#F59E0B");
                secondMarker.MarkerShape = MarkerShape.FilledCircle;
                secondMarker.MarkerSize = 12;
                secondMarker.IsVisible = false;
                pane.SecondMarkers[selected.StableKey] = secondMarker;
            }
            pane.Cursor = plot.Add.VerticalLine(0);
            pane.Cursor.Color = ScottPlot.Colors.White.WithAlpha(.7);
            pane.Cursor.LinePattern = LinePattern.Dotted;
            pane.Cursor.IsVisible = false;
            pane.SecondCursor = plot.Add.VerticalLine(0);
            pane.SecondCursor.Color = ScottPlot.Color.FromHex("#F59E0B");
            pane.SecondCursor.LinePattern = LinePattern.Dashed;
            pane.SecondCursor.LineWidth = 2;
            pane.SecondCursor.IsVisible = false;
            if (ShowLegendCheck.IsChecked == true && pane.Rendered.Count > 0) plot.ShowLegend(Alignment.UpperRight);
            plot.XLabel("Time (s)");
            if (!pane.HasRendered || AutoFollowCheck.IsChecked == true)
            {
                plot.Axes.AutoScale();
                plot.Axes.Margins(.02, .08);
                if (windowSeconds > 0 && latest > 0) plot.Axes.SetLimitsX(Math.Max(0, latest - windowSeconds), latest);
            }
            else plot.Axes.SetLimits(oldLimits);
            pane.HasRendered = true;
            if (_cursorTime.HasValue && _cursorGroup == pane.Group && pane.Rendered.Count > 0)
            {
                ShowMultiPointer(pane, _cursorTime.Value, false);
                if (_dualCursorEnabled && _secondCursorTime.HasValue) ShowSecondPointer(pane, _secondCursorTime.Value, false);
            }
            pane.Control.Refresh();
        }
        UpdatePaneHeights();
        if (!_cursorTime.HasValue)
        {
            PlotStatusText.Text = _displayPaused
                ? $"显示已暂停 · {_pausedSeries.Values.Sum(series => series.Xs.Length):N0}个信号采样点；后台CAN接收继续。"
                : $"{_viewModel.FrameCountText}帧 · {_viewModel.SelectedSignals.Count}个信号 · {_panes.Count}个坐标图。";
        }
    }

    private void MultiPlot_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_interactionMode != CanPlotInteractionMode.Pointer || _cursorLocked || e.RightButton == MouseButtonState.Pressed || sender is not WpfPlot control) return;
        var pane = FindPane(control);
        if (pane is null || pane.Group != _activeGroup || pane.Rendered.Count == 0) return;
        var coordinate = control.Plot.GetCoordinates(control.GetPlotPixelPosition(e));
        ShowMultiPointer(pane, coordinate.X, true);
    }

    private void MultiPlot_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        var pane = FindPane(control);
        if (pane is null) return;
        SetActiveGroup(pane.Group);
        Focus();
        Keyboard.Focus(this);
        if (_interactionMode != CanPlotInteractionMode.Sample || pane.Rendered.Count == 0) return;
        var coordinate = control.Plot.GetCoordinates(control.GetPlotPixelPosition(e));
        _cursorLocked = true;
        if (_dualCursorEnabled && _cursorTime.HasValue)
        {
            _activeSampleCursor = CanSampleCursor.B;
            ShowSecondPointer(pane, coordinate.X, true);
        }
        else
        {
            _activeSampleCursor = CanSampleCursor.A;
            ShowMultiPointer(pane, coordinate.X, true);
        }
        e.Handled = true;
    }

    private void ShowMultiPointer(CanPlotPane pane, double requestedTime, bool refresh)
    {
        if (pane.Cursor is null || pane.Rendered.Count == 0) return;
        var reference = pane.Rendered.Values.FirstOrDefault(series => series.Xs.Length > 0);
        if (reference is null) return;
        var referenceIndex = FindNearestIndex(reference.Xs, requestedTime);
        var cursorTime = reference.Xs[referenceIndex];
        _cursorTime = cursorTime;
        _cursorGroup = pane.Group;
        pane.Cursor.Position = cursorTime;
        pane.Cursor.IsVisible = true;
        var lines = new List<string> { _interactionMode == CanPlotInteractionMode.Sample ? "● 已锁定CAN采样" : $"CAN指针 · 图{pane.Group}", $"TIME  {cursorTime:F6} s" };
        foreach (var series in pane.Rendered.Values.Take(MaximumSignals))
        {
            var index = FindNearestIndex(series.Xs, cursorTime);
            var unit = string.IsNullOrWhiteSpace(series.Signal.Unit) ? string.Empty : $" {series.Signal.Unit}";
            lines.Add($"{series.Signal.IdText}/{series.Signal.Name}  {series.Ys[index]:G9}{unit}");
            if (pane.Markers.TryGetValue(series.Signal.StableKey, out var marker))
            {
                marker.Position = new Coordinates(series.Xs[index], series.Ys[index]);
                marker.IsVisible = true;
            }
        }
        if (_dualCursorEnabled && _secondCursorTime.HasValue)
        {
            UpdateCanComparisonTip(pane);
            if (refresh) pane.Control.Refresh();
            return;
        }
        pane.DataTip.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));
        pane.DataTipText.Text = string.Join(Environment.NewLine, lines);
        pane.DataTip.Visibility = Visibility.Visible;
        PlotStatusText.Text = string.Join("  |  ", lines.Skip(1));
        if (refresh) pane.Control.Refresh();
    }

    private void ShowSecondPointer(CanPlotPane pane, double requestedTime, bool refresh)
    {
        if (!_cursorTime.HasValue || pane.SecondCursor is null || pane.Rendered.Count == 0) return;
        var reference = pane.Rendered.Values.FirstOrDefault(series => series.Xs.Length > 0);
        if (reference is null) return;
        var index = FindNearestIndex(reference.Xs, requestedTime);
        _secondCursorTime = reference.Xs[index];
        _cursorGroup = pane.Group;
        pane.SecondCursor.Position = _secondCursorTime.Value;
        pane.SecondCursor.IsVisible = true;
        foreach (var series in pane.Rendered.Values)
        {
            var seriesIndex = FindNearestIndex(series.Xs, _secondCursorTime.Value);
            if (pane.SecondMarkers.TryGetValue(series.Signal.StableKey, out var marker))
            {
                marker.Position = new Coordinates(series.Xs[seriesIndex], series.Ys[seriesIndex]);
                marker.IsVisible = true;
            }
        }
        UpdateCanComparisonTip(pane);
        if (refresh) pane.Control.Refresh();
    }

    private void UpdateCanComparisonTip(CanPlotPane pane)
    {
        if (!_cursorTime.HasValue || !_secondCursorTime.HasValue) return;
        var deltaTime = _secondCursorTime.Value - _cursorTime.Value;
        var activeA = _activeSampleCursor == CanSampleCursor.A ? " ◀活动" : string.Empty;
        var activeB = _activeSampleCursor == CanSampleCursor.B ? " ◀活动" : string.Empty;
        var lines = new List<string>
        {
            $"A  {_cursorTime.Value:F6} s{activeA}",
            $"B  {_secondCursorTime.Value:F6} s{activeB}",
            $"Δt = {deltaTime:+0.000000;-0.000000;0.000000} s",
            "────────────────────────",
        };
        foreach (var series in pane.Rendered.Values.Take(MaximumSignals))
        {
            var firstIndex = FindNearestIndex(series.Xs, _cursorTime.Value);
            var secondIndex = FindNearestIndex(series.Xs, _secondCursorTime.Value);
            var first = series.Ys[firstIndex];
            var second = series.Ys[secondIndex];
            var delta = second - first;
            var unit = string.IsNullOrWhiteSpace(series.Signal.Unit) ? string.Empty : $" {series.Signal.Unit}";
            lines.Add($"{series.Signal.Name}  A={first:G8}  B={second:G8}");
            lines.Add($"  ΔY={delta:+0.######;-0.######;0}{unit}");
        }
        pane.DataTip.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        pane.DataTipText.Text = string.Join(Environment.NewLine, lines);
        pane.DataTip.Visibility = Visibility.Visible;
        PlotStatusText.Text = $"图{pane.Group}双游标：Δt={deltaTime:+0.000000;-0.000000;0.000000}s；活动游标={_activeSampleCursor}。";
    }

    private void MultiPlot_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        var pane = FindPane(control);
        if (pane is null) return;
        if (pane.Group != _activeGroup)
        {
            PlotStatusText.Text = $"请先左键选中图{pane.Group}，再使用滚轮缩放。";
            e.Handled = true;
            return;
        }
        AutoFollowCheck.IsChecked = false;
        var pixel = control.GetPlotPixelPosition(e);
        var rect = control.Plot.LastRender.DataRect;
        var factor = WheelZoom.FactorForDelta(e.Delta);
        var overX = pixel.Y > rect.Bottom && pixel.X >= rect.Left && pixel.X <= rect.Right;
        var overY = pixel.X < rect.Left && pixel.Y >= rect.Top && pixel.Y <= rect.Bottom;
        control.Plot.Axes.Zoom(pixel, overY ? 1 : factor, overX ? 1 : factor);
        control.Refresh();
        e.Handled = true;
    }

    private void MultiPlot_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        AutoFollowCheck.IsChecked = false;
        control.Cursor = Cursors.Hand;
    }

    private void MultiPlot_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfPlot control) control.Cursor = _interactionMode == CanPlotInteractionMode.RectangleZoom ? Cursors.Cross : Cursors.Cross;
    }

    private void ConfigureInteraction(WpfPlot control)
    {
        control.Cursor = Cursors.Cross;
        var input = control.UserInputProcessor;
        input.Reset();
        input.RemoveAll<MouseInteractWithPlottables>();
        input.RemoveAll<MouseDragPan>();
        input.RemoveAll<MouseDragZoom>();
        input.RemoveAll<MouseDragZoomRectangle>();
        input.RemoveAll<MouseWheelZoom>();
        input.RemoveAll<SingleClickAutoscale>();
        input.RemoveAll<SingleClickContextMenu>();
        input.RemoveAll<DoubleClickBenchmark>();
        input.UserActionResponses.Add(new MouseDragPan(ScottPlot.Interactivity.StandardMouseButtons.Right));
        if (_interactionMode == CanPlotInteractionMode.RectangleZoom)
        {
            input.UserActionResponses.Add(new MouseDragZoomRectangle(ScottPlot.Interactivity.StandardMouseButtons.Left));
        }
        input.Enable();
    }

    private void ConfigureAllInteractions()
    {
        foreach (var pane in _panes.Values) ConfigureInteraction(pane.Control);
    }

    private CanPlotPane? FindPane(WpfPlot control) => _panes.Values.FirstOrDefault(pane => ReferenceEquals(pane.Control, control));

    private void SetActiveGroup(int group)
    {
        _activeGroup = Math.Clamp(group, 1, PlotGroupOptions.Count);
        UpdateActiveBorders();
    }

    private void UpdateActiveBorders()
    {
        foreach (var pane in _panes.Values) pane.Border.BorderBrush = GroupBrush(pane.Group == _activeGroup);
    }

    private void UpdatePaneHeights()
    {
        if (_panes.Count == 0) return;
        var available = Math.Max(260, PlotScroll.ActualHeight - 10);
        var visibleCount = _activePlotMaximized ? 1 : _panes.Count;
        var height = visibleCount <= 2 ? Math.Max(260, available / visibleCount - 7) : 270;
        foreach (var pane in _panes.Values.Where(pane => pane.Border.Visibility == Visibility.Visible)) pane.Border.Height = height;
    }

    private void HideMultiPointer()
    {
        _cursorTime = null;
        _secondCursorTime = null;
        _activeSampleCursor = CanSampleCursor.A;
        foreach (var pane in _panes.Values)
        {
            if (pane.Cursor is not null) pane.Cursor.IsVisible = false;
            if (pane.SecondCursor is not null) pane.SecondCursor.IsVisible = false;
            foreach (var marker in pane.Markers.Values) marker.IsVisible = false;
            foreach (var marker in pane.SecondMarkers.Values) marker.IsVisible = false;
            pane.DataTip.Visibility = Visibility.Collapsed;
            pane.Control.Refresh();
        }
    }

    private void AddPlotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlotGroupOptions.Count >= 4) { PlotStatusText.Text = "最多创建4个CAN坐标图。"; return; }
        PlotGroupOptions.Add(PlotGroupOptions.Count + 1);
        _activeGroup = PlotGroupOptions.Count;
        BuildPlotPanes();
    }

    private void RemovePlotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlotGroupOptions.Count <= 1) return;
        var last = PlotGroupOptions[^1];
        if (_viewModel?.SelectedSignals.Any(signal => signal.PlotGroup == last) == true)
        {
            PlotStatusText.Text = $"图{last}仍有信号，请先把信号移动到其他图。";
            return;
        }
        PlotGroupOptions.RemoveAt(PlotGroupOptions.Count - 1);
        _activeGroup = Math.Min(_activeGroup, PlotGroupOptions.Count);
        BuildPlotPanes();
    }

    private void RectangleZoomModeButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _interactionMode = CanPlotInteractionMode.RectangleZoom;
        _sampleMode = false;
        _cursorLocked = false;
        if (DualCursorButton is not null) DualCursorButton.IsChecked = false;
        HideMultiPointer();
        ConfigureAllInteractions();
        AutoFollowCheck.IsChecked = false;
        PlotStatusText.Text = "框选放大：在活动图内按住左键拖出矩形；右键拖动仍可平移。";
    }

    private void DualCursorButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _dualCursorEnabled = true;
        _activeSampleCursor = CanSampleCursor.A;
        _secondCursorTime = null;
        if (SampleModeButton is not null && SampleModeButton.IsChecked != true) SampleModeButton.IsChecked = true;
        PlotStatusText.Text = "双游标：左键放置A，再次左键放置B；Tab切换活动游标，←/→逐采样移动。";
    }

    private void DualCursorButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        _dualCursorEnabled = false;
        _secondCursorTime = null;
        _activeSampleCursor = CanSampleCursor.A;
        foreach (var pane in _panes.Values)
        {
            if (pane.SecondCursor is not null) pane.SecondCursor.IsVisible = false;
            foreach (var marker in pane.SecondMarkers.Values) marker.IsVisible = false;
            pane.Control.Refresh();
        }
    }

    private void ClearCursorsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _secondCursorTime = null;
        _activeSampleCursor = CanSampleCursor.A;
        HideMultiPointer();
        PlotStatusText.Text = "已清除CAN采样游标。";
    }

    private void MaximizeCanPlotButton_OnClick(object sender, RoutedEventArgs e)
    {
        _activePlotMaximized = !_activePlotMaximized;
        MaximizeCanPlotButton.Content = _activePlotMaximized ? "显示全部" : "最大化";
        foreach (var pane in _panes.Values)
        {
            pane.Border.Visibility = !_activePlotMaximized || pane.Group == _activeGroup ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdatePaneHeights();
    }

    private void ContentViewMode_OnChanged(object sender, RoutedEventArgs e)
    {
        if (PlotRow is null || RawRow is null || ContentSplitterRow is null || PlotScroll is null || RawDetailsPanel is null || ContentSplitter is null) return;
        if (FramesViewModeButton?.IsChecked == true)
        {
            if (FocusWaveformButton?.IsChecked == true) FocusWaveformButton.IsChecked = false;
            PlotScroll.Visibility = Visibility.Collapsed;
            RawDetailsPanel.Visibility = Visibility.Visible;
            ContentSplitter.Visibility = Visibility.Collapsed;
            PlotRow.Height = new GridLength(0);
            ContentSplitterRow.Height = new GridLength(0);
            RawRow.Height = new GridLength(1, GridUnitType.Star);
            PlotStatusText.Text = "报文视图：原始帧表占满分析区域；可按当前ID过滤。";
        }
        else if (SplitViewModeButton?.IsChecked == true)
        {
            PlotScroll.Visibility = Visibility.Visible;
            RawDetailsPanel.Visibility = Visibility.Visible;
            ContentSplitter.Visibility = Visibility.Visible;
            PlotRow.Height = new GridLength(3, GridUnitType.Star);
            ContentSplitterRow.Height = new GridLength(6);
            RawRow.Height = new GridLength(2, GridUnitType.Star);
            PlotStatusText.Text = "分屏视图：拖动中间分隔条调整波形和报文表高度。";
        }
        else
        {
            PlotScroll.Visibility = Visibility.Visible;
            RawDetailsPanel.Visibility = Visibility.Collapsed;
            ContentSplitter.Visibility = Visibility.Collapsed;
            PlotRow.Height = new GridLength(1, GridUnitType.Star);
            ContentSplitterRow.Height = new GridLength(0);
            RawRow.Height = new GridLength(0);
            PlotStatusText.Text = "波形视图：图形使用完整分析区域；需要原始帧时切换到报文或分屏。";
        }
        Dispatcher.BeginInvoke(UpdatePaneHeights, DispatcherPriority.Loaded);
    }

    private void FocusWaveformButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (CatalogColumn is null || CatalogSplitterColumn is null) return;
        if (WaveformViewModeButton is not null) WaveformViewModeButton.IsChecked = true;
        if (CatalogColumn.Width.Value > 0) _catalogWidthBeforeFocus = CatalogColumn.Width;
        CatalogColumn.MinWidth = 0;
        CatalogColumn.Width = new GridLength(0);
        CatalogSplitterColumn.Width = new GridLength(0);
        FocusWaveformButton.Content = "显示目录";
        Dispatcher.BeginInvoke(UpdatePaneHeights, DispatcherPriority.Loaded);
    }

    private void FocusWaveformButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (CatalogColumn is null || CatalogSplitterColumn is null) return;
        CatalogColumn.MinWidth = 300;
        CatalogColumn.Width = _catalogWidthBeforeFocus.Value > 0 ? _catalogWidthBeforeFocus : new GridLength(400);
        CatalogSplitterColumn.Width = new GridLength(8);
        FocusWaveformButton.Content = "专注波形";
        Dispatcher.BeginInvoke(UpdatePaneHeights, DispatcherPriority.Loaded);
    }

    private static (double[] Xs, double[] Ys, int[] Segments) Downsample(double[] xs, double[] ys, int[] segments)
    {
        if (xs.Length <= MaximumRenderedPoints) return (xs, ys, segments);
        var step = (int)Math.Ceiling(xs.Length / (double)MaximumRenderedPoints);
        var outputX = new List<double>(MaximumRenderedPoints + 1);
        var outputY = new List<double>(MaximumRenderedPoints + 1);
        var outputSegments = new List<int>(MaximumRenderedPoints + 1);
        for (var index = 0; index < xs.Length; index += step)
        {
            outputX.Add(xs[index]);
            outputY.Add(ys[index]);
            outputSegments.Add(segments[index]);
        }
        if (outputX[^1] != xs[^1])
        {
            outputX.Add(xs[^1]);
            outputY.Add(ys[^1]);
            outputSegments.Add(segments[^1]);
        }
        return (outputX.ToArray(), outputY.ToArray(), outputSegments.ToArray());
    }

    private static (double[] Xs, double[] Ys) InsertSegmentBreaks(double[] xs, double[] ys, int[] segments)
    {
        if (xs.Length < 2 || segments.Length != xs.Length) return (xs, ys);
        var breakCount = Enumerable.Range(1, segments.Length - 1).Count(index => segments[index] != segments[index - 1]);
        if (breakCount == 0) return (xs, ys);
        var outputX = new List<double>(xs.Length + breakCount);
        var outputY = new List<double>(ys.Length + breakCount);
        for (var index = 0; index < xs.Length; index++)
        {
            if (index > 0 && segments[index] != segments[index - 1])
            {
                outputX.Add(xs[index]);
                outputY.Add(double.NaN);
            }
            outputX.Add(xs[index]);
            outputY.Add(ys[index]);
        }
        return (outputX.ToArray(), outputY.ToArray());
    }

    private static Brush GroupBrush(bool active) => new SolidColorBrush(active
        ? System.Windows.Media.Color.FromRgb(14, 165, 233)
        : System.Windows.Media.Color.FromRgb(38, 52, 75));

    private void RenderIfRequired()
    {
        if (!_dataDirty) return;
        RenderMultiPlots();
        _dataDirty = false;
    }

    private void RenderPlot()
    {
        if (_viewModel is null) return;
        var oldLimits = _hasRendered ? CanPlot.Plot.Axes.GetLimits() : default;
        var plot = CanPlot.Plot;
        plot.Clear();
        StylePlot(plot);
        _markers.Clear();
        _renderedSeries.Clear();
        var source = _displayPaused ? _pausedSeries : _series.ToDictionary(item => item.Key, item => new FrozenSeries(item.Value.Signal, item.Value.Xs.ToArray(), item.Value.Ys.ToArray()));
        var latest = source.Values.SelectMany(series => series.Xs.TakeLast(1)).DefaultIfEmpty(0).Max();
        var windowSeconds = ParseWindowSeconds();
        foreach (var selected in _viewModel.SelectedSignals)
        {
            if (!source.TryGetValue(selected.StableKey, out var full) || full.Xs.Length == 0) continue;
            var start = windowSeconds <= 0 ? 0 : Array.FindIndex(full.Xs, value => value >= latest - windowSeconds);
            if (start < 0) start = Math.Max(0, full.Xs.Length - 1);
            var xs = full.Xs[start..];
            var ys = full.Ys[start..];
            (xs, ys) = Downsample(xs, ys);
            var rendered = new FrozenSeries(selected, xs, ys);
            _renderedSeries[selected.StableKey] = rendered;
            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = ScottPlot.Color.FromHex(selected.ColorHex);
            scatter.LineWidth = 1.5f;
            scatter.MarkerSize = ShowPointsCheck.IsChecked == true ? 3 : 0;
            var latestValue = ys[^1].ToString("G7", CultureInfo.InvariantCulture);
            scatter.LegendText = string.IsNullOrWhiteSpace(selected.Unit) ? $"{selected.Name} = {latestValue}" : $"{selected.Name} = {latestValue} {selected.Unit}";
            var marker = plot.Add.Marker(0, 0);
            marker.Color = scatter.Color;
            marker.MarkerShape = _sampleMode ? MarkerShape.FilledCircle : MarkerShape.OpenCircle;
            marker.MarkerSize = _sampleMode ? 11 : 9;
            marker.IsVisible = false;
            _markers[selected.StableKey] = marker;
        }
        _cursorLine = plot.Add.VerticalLine(0);
        _cursorLine.Color = ScottPlot.Colors.White.WithAlpha(.7);
        _cursorLine.LinePattern = LinePattern.Dotted;
        _cursorLine.IsVisible = false;
        if (ShowLegendCheck.IsChecked == true && _renderedSeries.Count > 0) plot.ShowLegend(Alignment.UpperRight);
        plot.XLabel("Time (s)");
        if (!_hasRendered || AutoFollowCheck.IsChecked == true)
        {
            plot.Axes.AutoScale();
            plot.Axes.Margins(.02, .08);
            if (windowSeconds > 0 && latest > 0) plot.Axes.SetLimitsX(Math.Max(0, latest - windowSeconds), latest);
        }
        else plot.Axes.SetLimits(oldLimits);
        _hasRendered = true;
        if (_cursorTime.HasValue && _renderedSeries.Count > 0) ShowPointer(_cursorTime.Value, false);
        CanPlot.Refresh();
        if (!_cursorTime.HasValue)
        {
            PlotStatusText.Text = _displayPaused
                ? $"显示已暂停 · {_pausedSeries.Values.Sum(series => series.Xs.Length):N0}个信号采样点；后台CAN接收继续。"
                : $"{_viewModel.FrameCountText}帧 · {_viewModel.SelectedSignals.Count}个绘图信号 · 右键平移 / 滚轮缩放。";
        }
    }

    private void PointerModeButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _interactionMode = CanPlotInteractionMode.Pointer;
        _sampleMode = false;
        _cursorLocked = false;
        if (DualCursorButton is not null) DualCursorButton.IsChecked = false;
        if (DataTip is not null) DataTip.Visibility = Visibility.Collapsed;
        HideMultiPointer();
        ConfigureAllInteractions();
        if (PlotStatusText is not null) PlotStatusText.Text = "指针：移动鼠标查看活动图最近采样；右键拖动平移。";
        _dataDirty = true;
    }

    private void SampleModeButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _interactionMode = CanPlotInteractionMode.Sample;
        _sampleMode = true;
        _cursorLocked = false;
        HideMultiPointer();
        ConfigureAllInteractions();
        if (PauseButton is not null && PauseButton.IsChecked != true) PauseButton.IsChecked = true;
        _dataDirty = true;
    }

    private void PauseButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _displayPaused = true;
        _pausedSeries.Clear();
        foreach (var pair in _series) _pausedSeries[pair.Key] = new FrozenSeries(pair.Value.Signal, pair.Value.Xs.ToArray(), pair.Value.Ys.ToArray(), pair.Value.SegmentIds.ToArray());
        if (PauseButton is not null) { PauseButton.Content = "继续"; PauseButton.Tag = "\uE768"; }
        _dataDirty = true;
    }

    private void PauseButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        _displayPaused = false;
        _pausedSeries.Clear();
        if (PauseButton is not null) { PauseButton.Content = "暂停"; PauseButton.Tag = "\uE769"; }
        if (AutoFollowCheck is not null) AutoFollowCheck.IsChecked = true;
        _cursorLocked = false;
        _dataDirty = true;
    }

    private void Plot_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_sampleMode || _cursorLocked || _renderedSeries.Count == 0 || e.RightButton == MouseButtonState.Pressed) return;
        var coordinate = CanPlot.Plot.GetCoordinates(CanPlot.GetPlotPixelPosition(e));
        ShowPointer(coordinate.X, true);
    }

    private void Plot_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
        if (!_sampleMode || _renderedSeries.Count == 0) return;
        var coordinate = CanPlot.Plot.GetCoordinates(CanPlot.GetPlotPixelPosition(e));
        _cursorLocked = true;
        ShowPointer(coordinate.X, true);
        e.Handled = true;
    }

    private void ShowPointer(double requestedTime, bool refresh)
    {
        if (_cursorLine is null || _renderedSeries.Count == 0) return;
        var reference = _renderedSeries.Values.FirstOrDefault(series => series.Xs.Length > 0);
        if (reference is null) return;
        var referenceIndex = FindNearestIndex(reference.Xs, requestedTime);
        var cursorTime = reference.Xs[referenceIndex];
        _cursorTime = cursorTime;
        _cursorLine.Position = cursorTime;
        _cursorLine.IsVisible = true;
        var lines = new List<string> { _sampleMode ? "● 已锁定CAN采样" : "CAN指针", $"TIME  {cursorTime:F6} s" };
        foreach (var series in _renderedSeries.Values.Take(MaximumSignals))
        {
            if (series.Xs.Length == 0) continue;
            var index = FindNearestIndex(series.Xs, cursorTime);
            var unit = string.IsNullOrWhiteSpace(series.Signal.Unit) ? string.Empty : $" {series.Signal.Unit}";
            lines.Add($"{series.Signal.IdText}/{series.Signal.Name}  {series.Ys[index]:G9}{unit}");
            if (_markers.TryGetValue(series.Signal.StableKey, out var marker))
            {
                marker.Position = new Coordinates(series.Xs[index], series.Ys[index]);
                marker.IsVisible = true;
            }
        }
        DataTipText.Text = string.Join(Environment.NewLine, lines);
        DataTip.Visibility = Visibility.Visible;
        PlotStatusText.Text = string.Join("  |  ", lines.Skip(1));
        if (refresh) CanPlot.Refresh();
    }

    private void Plot_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AutoFollowCheck.IsChecked = false;
        var pixel = CanPlot.GetPlotPixelPosition(e);
        var rect = CanPlot.Plot.LastRender.DataRect;
        var factor = WheelZoom.FactorForDelta(e.Delta);
        var overX = pixel.Y > rect.Bottom && pixel.X >= rect.Left && pixel.X <= rect.Right;
        var overY = pixel.X < rect.Left && pixel.Y >= rect.Top && pixel.Y <= rect.Bottom;
        CanPlot.Plot.Axes.Zoom(pixel, overY ? 1 : factor, overX ? 1 : factor);
        CanPlot.Refresh();
        e.Handled = true;
    }

    private void Plot_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        AutoFollowCheck.IsChecked = false;
        CanPlot.Cursor = Cursors.Hand;
    }

    private void Plot_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) => CanPlot.Cursor = Cursors.Cross;

    private void ConfigureInteraction()
    {
        CanPlot.Cursor = Cursors.Cross;
        var input = CanPlot.UserInputProcessor;
        input.Reset();
        input.RemoveAll<MouseInteractWithPlottables>();
        input.RemoveAll<MouseDragPan>();
        input.RemoveAll<MouseDragZoom>();
        input.RemoveAll<MouseDragZoomRectangle>();
        input.RemoveAll<MouseWheelZoom>();
        input.RemoveAll<SingleClickAutoscale>();
        input.RemoveAll<SingleClickContextMenu>();
        input.RemoveAll<DoubleClickBenchmark>();
        input.UserActionResponses.Add(new MouseDragPan(ScottPlot.Interactivity.StandardMouseButtons.Right));
        input.Enable();
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs e)
    {
        AutoFollowCheck.IsChecked = true;
        foreach (var pane in _panes.Values) pane.HasRendered = false;
        _dataDirty = true;
    }

    private void FitYButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_panes.TryGetValue(_activeGroup, out var pane) || pane.Rendered.Count == 0) return;
        var limits = pane.Control.Plot.Axes.GetLimits();
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var series in pane.Rendered.Values)
        {
            for (var index = 0; index < series.Xs.Length; index++)
            {
                if (series.Xs[index] < limits.Left || series.Xs[index] > limits.Right || !double.IsFinite(series.Ys[index])) continue;
                min = Math.Min(min, series.Ys[index]);
                max = Math.Max(max, series.Ys[index]);
            }
        }
        if (!double.IsFinite(min) || !double.IsFinite(max)) return;
        var padding = Math.Max((max - min) * .08, Math.Max(Math.Abs(max), 1) * .01);
        pane.Control.Plot.Axes.SetLimitsY(min - padding, max + padding);
        pane.Control.Refresh();
    }

    private void DisplayOption_OnChanged(object sender, RoutedEventArgs e) => _dataDirty = true;

    private void ExportPngButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_panes.TryGetValue(_activeGroup, out var pane)) return;
        var dialog = new SaveFileDialog { Title = "导出CAN信号波形", Filter = "PNG图片 (*.png)|*.png", FileName = $"CAN_Waveform_{DateTime.Now:yyyyMMdd_HHmmss}.png" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        pane.Control.Plot.SavePng(dialog.FileName, 1800, 1000);
        PlotStatusText.Text = $"已导出：{dialog.FileName}";
    }

    private void HostWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ButtonBase) return;
        if (e.Key == Key.Space) { PauseButton.IsChecked = PauseButton.IsChecked != true; e.Handled = true; }
        else if (e.Key == Key.Home) { HomeButton_OnClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (_interactionMode == CanPlotInteractionMode.Sample && e.Key == Key.Tab && _dualCursorEnabled && _secondCursorTime.HasValue)
        {
            _activeSampleCursor = _activeSampleCursor == CanSampleCursor.A ? CanSampleCursor.B : CanSampleCursor.A;
            if (_panes.TryGetValue(_cursorGroup, out var activePane)) UpdateCanComparisonTip(activePane);
            e.Handled = true;
        }
        else if (_interactionMode == CanPlotInteractionMode.Sample && _cursorTime.HasValue && e.Key is Key.Left or Key.Right)
        {
            if (!_panes.TryGetValue(_cursorGroup, out var pane)) return;
            var reference = pane.Rendered.Values.FirstOrDefault(series => series.Xs.Length > 0);
            if (reference is null) return;
            var current = _activeSampleCursor == CanSampleCursor.B && _secondCursorTime.HasValue ? _secondCursorTime.Value : _cursorTime.Value;
            var index = Math.Clamp(FindNearestIndex(reference.Xs, current) + (e.Key == Key.Right ? 1 : -1), 0, reference.Xs.Length - 1);
            if (_activeSampleCursor == CanSampleCursor.B && _dualCursorEnabled) ShowSecondPointer(pane, reference.Xs[index], true);
            else ShowMultiPointer(pane, reference.Xs[index], true);
            e.Handled = true;
        }
    }

    private double ParseWindowSeconds()
    {
        var tag = (WindowCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    }

    private static (double[] Xs, double[] Ys) Downsample(double[] xs, double[] ys)
    {
        if (xs.Length <= MaximumRenderedPoints) return (xs, ys);
        var step = (int)Math.Ceiling(xs.Length / (double)MaximumRenderedPoints);
        var outputX = new List<double>(MaximumRenderedPoints + 1);
        var outputY = new List<double>(MaximumRenderedPoints + 1);
        for (var index = 0; index < xs.Length; index += step) { outputX.Add(xs[index]); outputY.Add(ys[index]); }
        if (outputX[^1] != xs[^1]) { outputX.Add(xs[^1]); outputY.Add(ys[^1]); }
        return (outputX.ToArray(), outputY.ToArray());
    }

    private static int FindNearestIndex(double[] values, double target)
    {
        var index = Array.BinarySearch(values, target);
        if (index >= 0) return index;
        index = ~index;
        if (index <= 0) return 0;
        if (index >= values.Length) return values.Length - 1;
        return Math.Abs(values[index] - target) < Math.Abs(values[index - 1] - target) ? index : index - 1;
    }

    private static void StylePlot(Plot plot)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0F172A");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#0F172A");
        plot.Axes.Color(ScottPlot.Color.FromHex("#94A3B8"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#273449");
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#172033");
        plot.Legend.FontColor = ScottPlot.Color.FromHex("#E8EEF8");
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#334155");
    }

    private sealed class SeriesCache(CanSignalItemViewModel signal)
    {
        public CanSignalItemViewModel Signal { get; } = signal;
        public List<double> Xs { get; } = [];
        public List<double> Ys { get; } = [];
        public List<int> SegmentIds { get; } = [];
    }

    private sealed class FrozenSeries(CanSignalItemViewModel signal, double[] xs, double[] ys, int[]? segmentIds = null)
    {
        public CanSignalItemViewModel Signal { get; } = signal;
        public double[] Xs { get; } = xs;
        public double[] Ys { get; } = ys;
        public int[] SegmentIds { get; } = segmentIds is { Length: > 0 } ? segmentIds : new int[xs.Length];
    }

    private sealed class CanPlotPane(int group, Border border, WpfPlot control, TextBlock title, Border dataTip, TextBlock dataTipText)
    {
        public int Group { get; } = group;
        public Border Border { get; } = border;
        public WpfPlot Control { get; } = control;
        public TextBlock Title { get; } = title;
        public Border DataTip { get; } = dataTip;
        public TextBlock DataTipText { get; } = dataTipText;
        public Dictionary<string, FrozenSeries> Rendered { get; } = [];
        public Dictionary<string, Marker> Markers { get; } = [];
        public Dictionary<string, Marker> SecondMarkers { get; } = [];
        public VerticalLine? Cursor { get; set; }
        public VerticalLine? SecondCursor { get; set; }
        public bool HasRendered { get; set; }
    }

    private enum CanPlotInteractionMode
    {
        Pointer,
        Sample,
        RectangleZoom,
    }

    private enum CanSampleCursor
    {
        A,
        B,
    }
}
