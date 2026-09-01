using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using V3RttMonitor.Core.Protocol;
using V3RttMonitor.Core.Visualization;

namespace V3RttMonitor.App.Views;

public partial class WaveformAnalysisView : UserControl
{
    private const int MaximumPlots = 8;
    private const int MaximumRenderedPoints = 50_000;
    private readonly DispatcherTimer _renderTimer;
    private readonly Dictionary<int, PlotPane> _panes = [];
    private MainViewModel? _viewModel;
    private Window? _hostWindow;
    private ListCollectionView? _fieldView;
    private bool _subscribed;
    private bool _layoutDirty = true;
    private volatile bool _dataDirty = true;
    private bool _routingNewSignals;
    private bool _activePlotMaximized;
    private bool _displayPaused;
    private RttFrame[] _pausedFrames = [];
    private int _activeGroup = 1;
    private PlotInteractionMode _interactionMode = PlotInteractionMode.Pointer;
    private int _pointerGroup = 1;
    private double? _pointerTimeSeconds;
    private bool _dualCursorEnabled;
    private double? _secondCursorTimeSeconds;
    private SampleCursor _activeSampleCursor = SampleCursor.A;
    private HashSet<int> _knownSelectedIndices = [];

    public WaveformAnalysisView()
    {
        InitializeComponent();
        PlotGroupOptions = [1];
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainViewModel);
        SizeChanged += (_, _) => UpdatePaneHeights();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _renderTimer.Tick += (_, _) => RenderIfRequired();
    }

    public ObservableCollection<int> PlotGroupOptions { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as MainViewModel);
        Subscribe();
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown += SampleCursor_OnPreviewKeyDown;
        _renderTimer.Start();
        _layoutDirty = true;
        RenderIfRequired();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer.Stop();
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown -= SampleCursor_OnPreviewKeyDown;
        _hostWindow = null;
        Unsubscribe();
    }

    private void AttachViewModel(MainViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            Subscribe();
            return;
        }
        Unsubscribe();
        _viewModel = viewModel;
        BuildFieldView();
        _knownSelectedIndices = _viewModel?.SelectedFields.Select(field => field.Index).ToHashSet() ?? [];
        Subscribe();
        _layoutDirty = true;
    }

    private void Subscribe()
    {
        if (_subscribed || _viewModel is null || !IsLoaded) return;
        _viewModel.AnalysisDataChanged += ViewModel_OnAnalysisDataChanged;
        _viewModel.SignalLayoutChanged += ViewModel_OnSignalLayoutChanged;
        _viewModel.Fields.CollectionChanged += Fields_OnCollectionChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _viewModel is null) return;
        _viewModel.AnalysisDataChanged -= ViewModel_OnAnalysisDataChanged;
        _viewModel.SignalLayoutChanged -= ViewModel_OnSignalLayoutChanged;
        _viewModel.Fields.CollectionChanged -= Fields_OnCollectionChanged;
        _subscribed = false;
    }

    private void ViewModel_OnAnalysisDataChanged(object? sender, EventArgs e)
    {
        if (!_displayPaused) _dataDirty = true;
    }
    private void ViewModel_OnSignalLayoutChanged(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            _layoutDirty = true;
            return;
        }

        var selectedNow = _viewModel.SelectedFields.Select(field => field.Index).ToHashSet();
        var requiredGroups = Math.Clamp(_viewModel.Fields.Select(field => field.PlotGroup).DefaultIfEmpty(1).Max(), 1, MaximumPlots);
        while (PlotGroupOptions.Count < requiredGroups) PlotGroupOptions.Add(PlotGroupOptions.Count + 1);
        if (!_routingNewSignals)
        {
            var added = selectedNow.Except(_knownSelectedIndices).ToArray();
            if (added.Length > 0)
            {
                _routingNewSignals = true;
                foreach (var index in added)
                {
                    var field = _viewModel.Fields.FirstOrDefault(item => item.Index == index);
                    if (field is not null) field.PlotGroup = _activeGroup;
                }
                _routingNewSignals = false;
            }
        }
        _knownSelectedIndices = selectedNow;
        _layoutDirty = true;
    }

    private void Fields_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            BuildFieldView();
            _layoutDirty = true;
        });
    }

    private void BuildFieldView()
    {
        if (_viewModel is null)
        {
            SignalList.ItemsSource = null;
            return;
        }
        _fieldView = new ListCollectionView(_viewModel.Fields);
        _fieldView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FieldValueViewModel.Group)));
        _fieldView.Filter = FilterField;
        SignalList.ItemsSource = _fieldView;
    }

    private bool FilterField(object value)
    {
        if (value is not FieldValueViewModel field) return false;
        var search = SearchBox.Text.Trim();
        return search.Length == 0
            || field.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
            || field.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || field.Unit.Contains(search, StringComparison.OrdinalIgnoreCase)
            || field.Group.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => _fieldView?.Refresh();

    private void SignalCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not CheckBox { Tag: FieldValueViewModel field }) return;
        if (field.IsPlotted) field.PlotGroup = _activeGroup;
        _viewModel.NotifySignalLayoutChanged();
    }

    private void AddPlotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlotGroupOptions.Count >= MaximumPlots)
        {
            CursorText.Text = $"最多创建 {MaximumPlots} 个坐标图。";
            return;
        }
        var group = PlotGroupOptions.Count + 1;
        PlotGroupOptions.Add(group);
        SetActiveGroup(group);
        _layoutDirty = true;
    }

    private void RemovePlotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || PlotGroupOptions.Count <= 1) return;
        var removed = _activeGroup;
        foreach (var field in _viewModel.Fields)
        {
            if (field.PlotGroup == removed) field.PlotGroup = Math.Max(1, removed - 1);
            else if (field.PlotGroup > removed) field.PlotGroup--;
        }
        PlotGroupOptions.Remove(PlotGroupOptions[^1]);
        SetActiveGroup(Math.Min(removed, PlotGroupOptions.Count));
        _viewModel.NotifySignalLayoutChanged();
        _layoutDirty = true;
    }

    private void ClearSignalsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearAllSignals();
        CursorText.Text = "已取消全部信号显示；字段配置和已接收数据未删除。";
    }

    private void ClearDataButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.ClearHistoryCommand.CanExecute(null) == true)
        {
            _viewModel.ClearHistoryCommand.Execute(null);
            _pausedFrames = [];
            _dataDirty = true;
            CursorText.Text = "已清空接收/BIN历史、波形和统计；Excel配置及磁盘BIN文件保留。";
        }
    }

    private void CleanupMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 2;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
    }

    private void RenderIfRequired()
    {
        if (_viewModel is null) return;
        if (_layoutDirty)
        {
            RebuildPlotControls();
            _layoutDirty = false;
            _dataDirty = true;
        }
        if (_dataDirty)
        {
            RenderData();
            _dataDirty = false;
        }
    }

    private void RebuildPlotControls()
    {
        PlotPanel.Children.Clear();
        _panes.Clear();
        WpfPlot? first = null;
        foreach (var group in PlotGroupOptions)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = GroupBrush(group == _activeGroup),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(4),
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var title = new TextBlock
            {
                Text = $"图 {group}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 0, 3),
            };
            var control = new WpfPlot { Focusable = true };
            Grid.SetRow(control, 1);
            var dataTipText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 238, 248)),
            };
            var dataTip = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(238, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(70, 12, 12, 12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Child = dataTipText,
            };
            Grid.SetRow(dataTip, 1);
            Panel.SetZIndex(dataTip, 10);
            grid.Children.Add(title);
            grid.Children.Add(control);
            grid.Children.Add(dataTip);
            border.Child = grid;
            PlotPanel.Children.Add(border);

            control.PreviewMouseDown += (_, _) => SetActiveGroup(group);
            control.PreviewMouseLeftButtonDown += Plot_OnPreviewMouseLeftButtonDown;
            control.PreviewMouseRightButtonDown += Plot_OnPreviewMouseRightButtonDown;
            control.PreviewMouseRightButtonUp += Plot_OnPreviewMouseRightButtonUp;
            control.PreviewMouseWheel += Plot_OnPreviewMouseWheel;
            control.MouseMove += Plot_OnMouseMove;
            control.MouseLeave += Plot_OnMouseLeave;
            ApplyInteractionMode(control);
            var pane = new PlotPane(group, border, control, title, dataTip, dataTipText);
            _panes[group] = pane;

            if (first is null) first = control;
            else first.Plot.Axes.Link(control, true, false);
        }
        UpdatePaneHeights();
        UpdateActiveBorders();
        UpdatePaneVisibility();
    }

    private void RenderData()
    {
        if (_viewModel is null || _panes.Count == 0) return;
        var sourceFrames = _displayPaused ? _pausedFrames : _viewModel.GetAnalysisFramesSnapshot();
        var frames = FilterFrames(sourceFrames);
        var sampled = Downsample(frames);
        var xs = sampled.Select(frame => frame.TimeMs / 1000.0).ToArray();
        var sequences = sampled.Select(frame => frame.Sequence).ToArray();

        var pointerRestored = false;
        foreach (var pane in _panes.Values)
        {
            var oldLimits = pane.HasRendered ? pane.Control.Plot.Axes.GetLimits() : default;
            var plot = pane.Control.Plot;
            plot.Clear();
            StylePlot(plot);
            pane.Series.Clear();
            pane.Markers.Clear();
            pane.SecondMarkers.Clear();

            var fields = _viewModel.SelectedFields.Where(field => field.PlotGroup == pane.Group).ToArray();
            pane.Title.Text = $"图 {pane.Group}  ·  {fields.Length} 路";
            foreach (var field in fields)
            {
                var ys = sampled.Select(frame => field.Index < frame.Values.Length ? (double)frame.Values[field.Index] : double.NaN).ToArray();
                pane.Series[field.Index] = ys;
                var scatter = plot.Add.Scatter(xs, ys);
                var latestValue = ys.LastOrDefault(double.NaN);
                var latestText = double.IsFinite(latestValue)
                    ? latestValue.ToString(field.Descriptor.Format, CultureInfo.InvariantCulture)
                    : "-";
                scatter.LegendText = string.IsNullOrEmpty(field.Unit)
                    ? $"{field.Key} = {latestText}"
                    : $"{field.Key} = {latestText} {field.Unit}";
                scatter.Color = ScottPlot.Color.FromHex(field.ColorHex);
                scatter.LineWidth = 1.4f;
                scatter.MarkerSize = ShowPointsCheck.IsChecked == true ? 3 : 0;
                if (field.Descriptor.IntegerLike) scatter.ConnectStyle = ConnectStyle.StepHorizontal;

                var marker = plot.Add.Marker(0, 0);
                marker.Color = scatter.Color;
                marker.MarkerShape = MarkerShape.OpenCircle;
                marker.MarkerSize = 9;
                marker.IsVisible = false;
                pane.Markers.Add(marker);

                var secondMarker = plot.Add.Marker(0, 0);
                secondMarker.Color = ScottPlot.Color.FromHex("#F59E0B");
                secondMarker.MarkerShape = MarkerShape.FilledCircle;
                secondMarker.MarkerSize = 10;
                secondMarker.IsVisible = false;
                pane.SecondMarkers.Add(secondMarker);
            }

            pane.Cursor = plot.Add.VerticalLine(0);
            pane.Cursor.Color = ScottPlot.Colors.White.WithAlpha(.65);
            pane.Cursor.LinePattern = LinePattern.Dotted;
            pane.Cursor.IsVisible = false;
            pane.SecondCursor = plot.Add.VerticalLine(0);
            pane.SecondCursor.Color = ScottPlot.Color.FromHex("#F59E0B");
            pane.SecondCursor.LinePattern = LinePattern.Dashed;
            pane.SecondCursor.LineWidth = 2;
            pane.SecondCursor.IsVisible = false;
            if (ShowLegendCheck.IsChecked == true && fields.Length > 0) plot.ShowLegend(Alignment.UpperRight);
            plot.XLabel("TIME_MS (s)");

            if (!pane.HasRendered || AutoFollowCheck.IsChecked == true)
            {
                plot.Axes.AutoScale();
                plot.Axes.Margins(.02, .08);
            }
            else
            {
                plot.Axes.SetLimits(oldLimits);
            }

            pane.Xs = xs;
            pane.Sequences = sequences;
            pane.Fields = fields;
            pane.HasRendered = true;
            pane.Control.Refresh();
            if (_interactionMode is PlotInteractionMode.Pointer or PlotInteractionMode.Sample
                && _pointerTimeSeconds.HasValue
                && _pointerGroup == pane.Group
                && pane.Xs.Length > 0)
            {
                var requestedTime = _pointerTimeSeconds.Value;
                if (_interactionMode == PlotInteractionMode.Pointer && pane.Control.IsMouseOver)
                {
                    requestedTime = pane.Control.Plot.GetCoordinates(pane.Control.GetCurrentPlotPixelPosition()).X;
                }
                ShowPointerValue(pane, requestedTime, refresh: true);
                if (_dualCursorEnabled && _secondCursorTimeSeconds.HasValue)
                {
                    ShowSecondCursorValue(pane, _secondCursorTimeSeconds.Value, refresh: true);
                }
                pointerRestored = true;
            }
        }
        if (!pointerRestored)
        {
            CursorText.Text = _displayPaused
                ? $"显示已暂停 · 冻结 {sourceFrames.Length:N0} 帧，当前窗口 {frames.Length:N0} 帧；J-Link/TCP接收及BIN记录继续。"
                : $"保留 {frames.Length:N0} 帧，绘制 {sampled.Length:N0} 点/路；活动图：图 {_activeGroup}。";
        }
    }

    private RttFrame[] FilterFrames(RttFrame[] frames)
    {
        if (frames.Length == 0) return frames;
        var seconds = ParseWindowSeconds();
        if (seconds <= 0) return frames;
        var startTime = frames[^1].TimeMs - seconds * 1000;
        var start = Array.FindIndex(frames, frame => frame.TimeMs >= startTime);
        return start <= 0 ? frames : frames[start..];
    }

    private static RttFrame[] Downsample(RttFrame[] frames)
    {
        if (frames.Length <= MaximumRenderedPoints) return frames;
        var step = (int)Math.Ceiling(frames.Length / (double)MaximumRenderedPoints);
        var result = new List<RttFrame>(MaximumRenderedPoints + 1);
        for (var i = 0; i < frames.Length; i += step) result.Add(frames[i]);
        if (!ReferenceEquals(result[^1], frames[^1])) result.Add(frames[^1]);
        return result.ToArray();
    }

    private void Plot_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        var pane = _panes.Values.FirstOrDefault(item => ReferenceEquals(item.Control, control));
        if (pane is null) return;
        if (pane.Group != _activeGroup)
        {
            CursorText.Text = $"图 {pane.Group} 尚未选中；请先左键点击该图，再使用滚轮缩放。";
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
        CursorText.Text = e.Delta > 0
            ? "滚轮向上：已围绕鼠标位置放大。"
            : "滚轮向下：已围绕鼠标位置缩小。";
        e.Handled = true;
    }

    private void Plot_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        var pane = _panes.Values.FirstOrDefault(item => ReferenceEquals(item.Control, control));
        if (_interactionMode != PlotInteractionMode.Pointer
            || pane is null
            || pane.Group != _activeGroup
            || pane.Xs.Length == 0
            || pane.Fields.Length == 0
            || e.RightButton == MouseButtonState.Pressed) return;
        var coordinate = control.Plot.GetCoordinates(control.GetPlotPixelPosition(e));
        ShowPointerValue(pane, coordinate.X, refresh: true);
    }

    private void Plot_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_interactionMode != PlotInteractionMode.Sample || sender is not WpfPlot control) return;
        var pane = _panes.Values.FirstOrDefault(item => ReferenceEquals(item.Control, control));
        if (pane is null || pane.Xs.Length == 0 || pane.Fields.Length == 0) return;
        SetActiveGroup(pane.Group);
        Focus();
        Keyboard.Focus(this);
        var coordinate = control.Plot.GetCoordinates(control.GetPlotPixelPosition(e));
        if (_dualCursorEnabled && _pointerTimeSeconds.HasValue)
        {
            _activeSampleCursor = SampleCursor.B;
            ShowSecondCursorValue(pane, coordinate.X, refresh: true);
            CursorText.Text = "B游标已锁定；←/→逐点移动B，Tab切换活动游标，右键拖动平移。";
        }
        else
        {
            _activeSampleCursor = SampleCursor.A;
            ShowPointerValue(pane, coordinate.X, refresh: true);
            CursorText.Text = _dualCursorEnabled
                ? "A游标已锁定；再次左键放置B游标，←/→逐点移动，Tab切换A/B。"
                : "A游标已锁定；←/→逐点移动，右键拖动平移。";
        }
        e.Handled = true;
    }

    private void SampleCursor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or System.Windows.Controls.Primitives.ButtonBase) return;
        if (e.Key == Key.Space)
        {
            PauseDisplayButton.IsChecked = PauseDisplayButton.IsChecked != true;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Home)
        {
            HomeButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Y)
        {
            FitVisibleYButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C)
        {
            ClearCursorsButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (_interactionMode != PlotInteractionMode.Sample) return;
        var pane = ActivePane();
        if (pane is null || pane.Group != _pointerGroup || pane.Xs.Length == 0) return;

        if (e.Key == Key.Tab && _dualCursorEnabled && _secondCursorTimeSeconds.HasValue)
        {
            _activeSampleCursor = _activeSampleCursor == SampleCursor.A ? SampleCursor.B : SampleCursor.A;
            CursorText.Text = $"活动游标：{_activeSampleCursor}；←/→逐采样移动。";
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Left or Key.Right)) return;

        var direction = e.Key == Key.Right ? 1 : -1;
        if (_activeSampleCursor == SampleCursor.B && _dualCursorEnabled)
        {
            var current = _secondCursorTimeSeconds ?? _pointerTimeSeconds;
            if (!current.HasValue) return;
            var index = Math.Clamp(FindNearestIndex(pane.Xs, current.Value) + direction, 0, pane.Xs.Length - 1);
            ShowSecondCursorValue(pane, pane.Xs[index], refresh: true);
        }
        else
        {
            if (!_pointerTimeSeconds.HasValue) return;
            var index = Math.Clamp(FindNearestIndex(pane.Xs, _pointerTimeSeconds.Value) + direction, 0, pane.Xs.Length - 1);
            ShowPointerValue(pane, pane.Xs[index], refresh: true);
        }
        e.Handled = true;
    }

    private void Plot_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        AutoFollowCheck.IsChecked = false;
        control.Cursor = Cursors.Hand;
        CursorText.Text = "右键平移：拖动坐标范围；松开后恢复当前指针工具。";
    }

    private void Plot_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfPlot control) control.Cursor = Cursors.Cross;
    }

    private void ShowPointerValue(PlotPane pane, double requestedTime, bool refresh)
    {
        var index = FindNearestIndex(pane.Xs, requestedTime);
        _pointerGroup = pane.Group;
        _pointerTimeSeconds = pane.Xs[index];
        pane.Cursor.Position = pane.Xs[index];
        pane.Cursor.IsVisible = true;
        var parts = new List<string> { $"图{pane.Group}", $"SEQ={pane.Sequences[index]:N0}", $"t={pane.Xs[index]:F6}s" };
        for (var i = 0; i < pane.Fields.Length; i++)
        {
            var field = pane.Fields[i];
            var y = pane.Series[field.Index][index];
            pane.Markers[i].Position = new Coordinates(pane.Xs[index], y);
            pane.Markers[i].MarkerShape = _interactionMode == PlotInteractionMode.Sample
                ? MarkerShape.FilledCircle
                : MarkerShape.OpenCircle;
            pane.Markers[i].MarkerSize = _interactionMode == PlotInteractionMode.Sample ? 11 : 9;
            pane.Markers[i].IsVisible = true;
            parts.Add($"{field.Key}={y.ToString(field.Descriptor.Format, CultureInfo.InvariantCulture)}{field.Unit}");
        }
        if (_dualCursorEnabled && _secondCursorTimeSeconds.HasValue)
        {
            var secondIndex = FindNearestIndex(pane.Xs, _secondCursorTimeSeconds.Value);
            UpdateComparisonTip(pane, index, secondIndex);
        }
        else
        {
            UpdateSingleCursorTip(pane, index);
            CursorText.Text = string.Join("  |  ", parts);
        }
        if (refresh) pane.Control.Refresh();
    }

    private void ShowSecondCursorValue(PlotPane pane, double requestedTime, bool refresh)
    {
        if (!_pointerTimeSeconds.HasValue) return;
        var index = FindNearestIndex(pane.Xs, requestedTime);
        _pointerGroup = pane.Group;
        _secondCursorTimeSeconds = pane.Xs[index];
        pane.SecondCursor.Position = pane.Xs[index];
        pane.SecondCursor.IsVisible = true;
        for (var i = 0; i < pane.Fields.Length; i++)
        {
            var field = pane.Fields[i];
            var y = pane.Series[field.Index][index];
            pane.SecondMarkers[i].Position = new Coordinates(pane.Xs[index], y);
            pane.SecondMarkers[i].IsVisible = true;
        }
        var firstIndex = FindNearestIndex(pane.Xs, _pointerTimeSeconds.Value);
        UpdateComparisonTip(pane, firstIndex, index);
        if (refresh) pane.Control.Refresh();
    }

    private void UpdateSingleCursorTip(PlotPane pane, int index)
    {
        pane.DataTip.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));
        var tipLines = new List<string>
        {
            _interactionMode == PlotInteractionMode.Sample ? "● A游标采样点" : "指针采样",
            $"TIME  {pane.Xs[index]:F6} s",
            $"SEQ   {pane.Sequences[index]:N0}",
        };
        foreach (var field in pane.Fields.Take(6))
        {
            var value = pane.Series[field.Index][index];
            var unit = string.IsNullOrEmpty(field.Unit) ? string.Empty : $" {field.Unit}";
            tipLines.Add($"{field.Key}  {value.ToString(field.Descriptor.Format, CultureInfo.InvariantCulture)}{unit}");
        }
        if (pane.Fields.Length > 6) tipLines.Add($"… 另有 {pane.Fields.Length - 6} 路");
        pane.DataTipText.Text = string.Join(Environment.NewLine, tipLines);
        pane.DataTip.Visibility = Visibility.Visible;
    }

    private void UpdateComparisonTip(PlotPane pane, int firstIndex, int secondIndex)
    {
        pane.DataTip.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        var deltaTime = pane.Xs[secondIndex] - pane.Xs[firstIndex];
        var deltaSequence = pane.Sequences[secondIndex] - pane.Sequences[firstIndex];
        var activeA = _activeSampleCursor == SampleCursor.A ? " ◀活动" : string.Empty;
        var activeB = _activeSampleCursor == SampleCursor.B ? " ◀活动" : string.Empty;
        var lines = new List<string>
        {
            $"A  {pane.Xs[firstIndex]:F6} s  SEQ={pane.Sequences[firstIndex]:N0}{activeA}",
            $"B  {pane.Xs[secondIndex]:F6} s  SEQ={pane.Sequences[secondIndex]:N0}{activeB}",
            $"Δt = {deltaTime:+0.000000;-0.000000;0.000000} s   ΔSEQ = {deltaSequence:+#;-#;0}",
            "────────────────────────",
        };
        foreach (var field in pane.Fields.Take(6))
        {
            var first = pane.Series[field.Index][firstIndex];
            var second = pane.Series[field.Index][secondIndex];
            var delta = second - first;
            var unit = string.IsNullOrEmpty(field.Unit) ? string.Empty : $" {field.Unit}";
            lines.Add($"{field.Key}  A={FormatValue(field, first)}  B={FormatValue(field, second)}");
            lines.Add($"  ΔY={delta.ToString("+0.######;-0.######;0", CultureInfo.InvariantCulture)}{unit}");
        }
        if (pane.Fields.Length > 6) lines.Add($"… 另有 {pane.Fields.Length - 6} 路");
        pane.DataTipText.Text = string.Join(Environment.NewLine, lines);
        pane.DataTip.Visibility = Visibility.Visible;
        CursorText.Text = $"双游标：Δt={deltaTime:+0.000000;-0.000000;0.000000}s，ΔSEQ={deltaSequence:+#;-#;0}；活动游标={_activeSampleCursor}。";
    }

    private static string FormatValue(FieldValueViewModel field, double value) =>
        value.ToString(field.Descriptor.Format, CultureInfo.InvariantCulture);

    private void Plot_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not WpfPlot control) return;
        var pane = _panes.Values.FirstOrDefault(item => ReferenceEquals(item.Control, control));
        if (pane is null) return;
        // MATLAB-style data tips remain visible at the last inspected point.
        // The next MouseMove or live render refreshes the values in place.
    }

    private void SetActiveGroup(int group)
    {
        _activeGroup = Math.Clamp(group, 1, PlotGroupOptions.Count);
        SignalStatusText.Text = $"活动图：图 {_activeGroup}";
        UpdateActiveBorders();
        UpdatePaneVisibility();
    }

    private void UpdateActiveBorders()
    {
        foreach (var pane in _panes.Values) pane.Border.BorderBrush = GroupBrush(pane.Group == _activeGroup);
    }

    private void UpdatePaneHeights()
    {
        if (_panes.Count == 0) return;
        var available = Math.Max(260, PlotScroll.ActualHeight - 12);
        var visibleCount = _activePlotMaximized ? 1 : _panes.Count;
        var height = visibleCount <= 3 ? Math.Max(260, available / visibleCount - 7) : 270;
        foreach (var pane in _panes.Values) if (pane.Border.Visibility == Visibility.Visible) pane.Border.Height = height;
    }

    private void UpdatePaneVisibility()
    {
        foreach (var pane in _panes.Values)
        {
            pane.Border.Visibility = !_activePlotMaximized || pane.Group == _activeGroup
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        UpdatePaneHeights();
    }

    private void HidePointer()
    {
        _pointerTimeSeconds = null;
        _secondCursorTimeSeconds = null;
        _activeSampleCursor = SampleCursor.A;
        foreach (var pane in _panes.Values)
        {
            pane.Cursor.IsVisible = false;
            foreach (var marker in pane.Markers) marker.IsVisible = false;
            pane.SecondCursor.IsVisible = false;
            foreach (var marker in pane.SecondMarkers) marker.IsVisible = false;
            pane.DataTip.Visibility = Visibility.Collapsed;
            pane.Control.Refresh();
        }
    }

    private void PointerButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _interactionMode = PlotInteractionMode.Pointer;
        if (DualCursorButton is not null) DualCursorButton.IsChecked = false;
        HidePointer();
        ApplyInteractionMode();
        if (CursorText is not null) CursorText.Text = "指针已选中：移动鼠标查看最近采样值；按住右键拖动即可平移。";
    }

    private void SampleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _interactionMode = PlotInteractionMode.Sample;
        HidePointer();
        ApplyInteractionMode();
        if (PauseDisplayButton is not null && PauseDisplayButton.IsChecked != true)
        {
            PauseDisplayButton.IsChecked = true;
        }
        if (CursorText is not null) CursorText.Text = "点选采样已选中并暂停显示：左键点击曲线锁定最近采样点，右键拖动平移。";
    }

    private void RectangleZoomButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _interactionMode = PlotInteractionMode.RectangleZoom;
        if (DualCursorButton is not null) DualCursorButton.IsChecked = false;
        HidePointer();
        ApplyInteractionMode();
        if (AutoFollowCheck is not null) AutoFollowCheck.IsChecked = false;
        if (CursorText is not null) CursorText.Text = "框选放大：在活动图内按住左键拖出矩形；右键拖动仍可平移。";
    }

    private void DualCursorButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _dualCursorEnabled = true;
        if (SampleModeButton is not null && SampleModeButton.IsChecked != true)
        {
            SampleModeButton.IsChecked = true;
        }
        if (_pointerTimeSeconds.HasValue && ActivePane() is { } pane && pane.Xs.Length > 0)
        {
            _secondCursorTimeSeconds = _pointerTimeSeconds;
            _activeSampleCursor = SampleCursor.B;
            ShowSecondCursorValue(pane, _secondCursorTimeSeconds.Value, refresh: true);
        }
        Focus();
        Keyboard.Focus(this);
        if (CursorText is not null)
        {
            CursorText.Text = _pointerTimeSeconds.HasValue
                ? "双游标已开启，B与A重合；左键放置B，←/→移动，Tab切换A/B。"
                : "双游标已开启；先左键放置A，再次左键放置B。";
        }
    }

    private void DualCursorButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        _dualCursorEnabled = false;
        _secondCursorTimeSeconds = null;
        _activeSampleCursor = SampleCursor.A;
        foreach (var pane in _panes.Values)
        {
            pane.SecondCursor.IsVisible = false;
            foreach (var marker in pane.SecondMarkers) marker.IsVisible = false;
            if (_pointerTimeSeconds.HasValue && pane.Group == _pointerGroup && pane.Xs.Length > 0)
            {
                ShowPointerValue(pane, _pointerTimeSeconds.Value, refresh: false);
            }
            pane.Control.Refresh();
        }
        if (CursorText is not null && _interactionMode == PlotInteractionMode.Sample)
        {
            CursorText.Text = "已切换单游标；←/→逐采样移动A。";
        }
    }

    private void PauseDisplayButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || PauseDisplayButton is null) return;
        _pausedFrames = _viewModel.GetAnalysisFramesSnapshot();
        _displayPaused = true;
        AutoFollowCheck.IsChecked = false;
        PauseDisplayButton.Content = "继续";
        PauseDisplayButton.Tag = "\uE768";
        _dataDirty = true;
        CursorText.Text = $"显示已暂停：冻结 {_pausedFrames.Length:N0} 帧用于分析；J-Link/TCP接收及BIN记录继续运行。";
    }

    private void PauseDisplayButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (PauseDisplayButton is null) return;
        _displayPaused = false;
        _pausedFrames = [];
        PauseDisplayButton.Content = "暂停";
        PauseDisplayButton.Tag = "\uE769";
        if (DualCursorButton is not null) DualCursorButton.IsChecked = false;
        if (AutoFollowCheck is not null) AutoFollowCheck.IsChecked = true;
        HidePointer();
        _dataDirty = true;
        CursorText.Text = "已继续实时显示，波形回到最新数据；J-Link/TCP连接未重启。";
    }

    private void ApplyInteractionMode()
    {
        foreach (var pane in _panes.Values) ApplyInteractionMode(pane.Control);
    }

    private void ApplyInteractionMode(WpfPlot control)
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
        if (_interactionMode == PlotInteractionMode.RectangleZoom)
        {
            input.UserActionResponses.Add(new MouseDragZoomRectangle(ScottPlot.Interactivity.StandardMouseButtons.Left));
        }
        input.Enable();
    }

    private PlotPane? ActivePane() => _panes.GetValueOrDefault(_activeGroup);

    private void HomeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var pane = ActivePane();
        if (pane is null) return;
        pane.Control.Plot.Axes.AutoScale();
        pane.Control.Plot.Axes.Margins(.02, .08);
        pane.Control.Refresh();
    }

    private void FitVisibleYButton_OnClick(object sender, RoutedEventArgs e)
    {
        var pane = ActivePane();
        if (pane is null || pane.Xs.Length == 0 || pane.Series.Count == 0) return;
        var limits = pane.Control.Plot.Axes.GetLimits();
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var series in pane.Series.Values)
        {
            for (var i = 0; i < pane.Xs.Length && i < series.Length; i++)
            {
                if (pane.Xs[i] < limits.Left || pane.Xs[i] > limits.Right || !double.IsFinite(series[i])) continue;
                minimum = Math.Min(minimum, series[i]);
                maximum = Math.Max(maximum, series[i]);
            }
        }
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            CursorText.Text = "当前可见时间范围没有有效数据。";
            return;
        }
        var span = maximum - minimum;
        var margin = span > 0 ? span * .08 : Math.Max(1e-6, Math.Abs(maximum) * .05 + .5);
        pane.Control.Plot.Axes.SetLimitsY(minimum - margin, maximum + margin);
        AutoFollowCheck.IsChecked = false;
        pane.Control.Refresh();
        CursorText.Text = $"图 {_activeGroup} 已按当前可见时间范围适配Y轴。";
    }

    private void ClearCursorsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DualCursorButton.IsChecked == true) DualCursorButton.IsChecked = false;
        HidePointer();
        CursorText.Text = "已清除A/B游标和数据提示。";
    }

    private void MaximizePlotButton_OnClick(object sender, RoutedEventArgs e)
    {
        _activePlotMaximized = !_activePlotMaximized;
        MaximizePlotButton.Content = _activePlotMaximized ? "全部图" : "最大化";
        MaximizePlotButton.Background = _activePlotMaximized
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(7, 89, 133))
            : (Brush)FindResource("PanelRaisedBrush");
        UpdatePaneVisibility();
    }

    private void DisplayOption_OnChanged(object sender, EventArgs e)
    {
        if (IsLoaded) _dataDirty = true;
    }

    private void ExportPngButton_OnClick(object sender, RoutedEventArgs e)
    {
        var pane = ActivePane();
        if (pane is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出活动坐标图",
            Filter = "PNG图片 (*.png)|*.png",
            FileName = $"JustFloat_Plot{pane.Group}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dialog.ShowDialog() != true) return;
        pane.Control.Plot.SavePng(dialog.FileName, 1600, 900);
        CursorText.Text = $"已导出图 {pane.Group}：{dialog.FileName}";
    }

    private double ParseWindowSeconds()
    {
        var tag = (WindowCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
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

    private static Brush GroupBrush(bool active) => new SolidColorBrush(active
        ? System.Windows.Media.Color.FromRgb(14, 165, 233)
        : System.Windows.Media.Color.FromRgb(38, 52, 75));

    private static int FindNearestIndex(double[] values, double target)
    {
        var index = Array.BinarySearch(values, target);
        if (index >= 0) return index;
        index = ~index;
        if (index <= 0) return 0;
        if (index >= values.Length) return values.Length - 1;
        return Math.Abs(values[index] - target) < Math.Abs(values[index - 1] - target) ? index : index - 1;
    }

    private sealed class PlotPane(int group, Border border, WpfPlot control, TextBlock title, Border dataTip, TextBlock dataTipText)
    {
        public int Group { get; } = group;
        public Border Border { get; } = border;
        public WpfPlot Control { get; } = control;
        public TextBlock Title { get; } = title;
        public Border DataTip { get; } = dataTip;
        public TextBlock DataTipText { get; } = dataTipText;
        public Dictionary<int, double[]> Series { get; } = [];
        public List<Marker> Markers { get; } = [];
        public List<Marker> SecondMarkers { get; } = [];
        public VerticalLine Cursor { get; set; } = new();
        public VerticalLine SecondCursor { get; set; } = new();
        public FieldValueViewModel[] Fields { get; set; } = [];
        public double[] Xs { get; set; } = [];
        public long[] Sequences { get; set; } = [];
        public bool HasRendered { get; set; }
    }

    private enum PlotInteractionMode { Pointer, Sample, RectangleZoom }
    private enum SampleCursor { A, B }
}
