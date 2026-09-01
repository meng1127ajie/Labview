using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using V3RttMonitor.App.ViewModels;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App;

public partial class AcquisitionWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly MainWindow _mainWindow;
    private bool _allowClose;
    private bool _showingWaveform;
    private bool _navigateToWaveformWhenReady;

    public AcquisitionWindow(MainViewModel viewModel, MainWindow mainWindow)
    {
        _viewModel = viewModel;
        _mainWindow = mainWindow;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.Fields.CollectionChanged += Fields_OnCollectionChanged;
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
    }

    public void ShowWorkspace()
    {
        ShowConnectionWorkspace();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Activate();
        Focus();
    }

    public void ShowWaveformWorkspace()
    {
        if (!IsVisible) Show();
        if (_viewModel.Fields.Count == 0)
        {
            ShowConnectionWorkspace();
        }
        else
        {
            _showingWaveform = true;
            ConnectionPanel.Visibility = Visibility.Collapsed;
            StatsPanel.Visibility = Visibility.Collapsed;
            PreviewPanel.Visibility = Visibility.Collapsed;
            FooterText.Visibility = Visibility.Collapsed;
            EmbeddedWaveform.Visibility = Visibility.Visible;
            BackButton.Content = "返回连接设置";
            WorkspaceTitle.Text = "实时波形分析";
            WorkspaceSubtitle.Text = "数据来源保持连接；返回时回到当前连接设置";
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Activate();
        Focus();
    }

    private void ShowConnectionWorkspace()
    {
        _showingWaveform = false;
        _navigateToWaveformWhenReady = false;
        EmbeddedWaveform.Visibility = Visibility.Collapsed;
        ConnectionPanel.Visibility = Visibility.Visible;
        StatsPanel.Visibility = Visibility.Visible;
        PreviewPanel.Visibility = Visibility.Visible;
        FooterText.Visibility = Visibility.Visible;
        BackButton.Content = "返回启动中心";
        WorkspaceTitle.Text = "J-Link 数据采集";
        WorkspaceSubtitle.Text = "先连接与接收，再进入同一工作台内的波形分析";
    }

    public void ReturnToHome()
    {
        if (_allowClose) return;
        Hide();
        _mainWindow.ShowHome();
    }

    public void ForceClose()
    {
        _allowClose = true;
        _viewModel.Fields.CollectionChanged -= Fields_OnCollectionChanged;
        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        Close();
    }

    private void ConnectionSourceCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionSourceCombo?.SelectedItem is not ComboBoxItem item || JLinkPanel is null || TcpPanel is null || HssPanel is null) return;
        var mode = item.Tag?.ToString() switch
        {
            "Hss" => ConnectionMode.Hss,
            "TcpDirect" => ConnectionMode.TcpDirect,
            _ => ConnectionMode.JLinkRtt,
        };
        JLinkPanel.Visibility = mode == ConnectionMode.JLinkRtt ? Visibility.Visible : Visibility.Collapsed;
        TcpPanel.Visibility = mode == ConnectionMode.TcpDirect ? Visibility.Visible : Visibility.Collapsed;
        HssPanel.Visibility = mode == ConnectionMode.Hss ? Visibility.Visible : Visibility.Collapsed;
        _viewModel.ConnectionMode = mode;
    }

    private void ConnectButton_OnClick(object sender, RoutedEventArgs e) => _navigateToWaveformWhenReady = true;

    private void Fields_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => TryNavigateToWaveform();

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected)) TryNavigateToWaveform();
    }

    private void TryNavigateToWaveform()
    {
        if (!_navigateToWaveformWhenReady || !_viewModel.IsConnected || _viewModel.Fields.Count == 0) return;
        _navigateToWaveformWhenReady = false;
        Dispatcher.BeginInvoke(ShowWaveformWorkspace);
    }

    private async void HssBrowseElfButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择HSS应用ELF", Filter = "ARM ELF (*.elf;*.axf;*.out)|*.elf;*.axf;*.out|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) await _viewModel.LoadHssElfFileAsync(dialog.FileName);
    }

    private void HssSelectVariablesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HssCatalog is null)
        {
            MessageBox.Show(this, "请先选择并解析ELF文件。", "HSS变量", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new HssSymbolSelectionWindow(_viewModel.HssCatalog, _viewModel.HssVariables) { Owner = this };
        if (dialog.ShowDialog() == true) _viewModel.ApplyHssVariables(dialog.SelectedVariables);
    }

    private void HssValidateButton_OnClick(object sender, RoutedEventArgs e) => _viewModel.ValidateHssEnvironment();

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var template = files.FirstOrDefault(IsExcelTemplate);
        if (template is not null) await _viewModel.LoadTemplateFileAsync(template);
        var elf = files.FirstOrDefault(IsElfFile);
        if (elf is not null) await _viewModel.LoadHssElfFileAsync(elf);
        var bin = files.FirstOrDefault(path => Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase));
        if (bin is not null) await _viewModel.LoadBinaryFileAsync(bin);
        var dbcFiles = files.Where(path => Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase)).ToArray();
        var canLogs = files.Where(CanAnalysisWindow.IsCanLog).ToArray();
        if (dbcFiles.Length > 0 || canLogs.Length > 0)
        {
            Hide();
            _mainWindow.OpenCanOfflineAnalysis();
            if (dbcFiles.Length > 0) await _mainWindow.CanViewModel.LoadDbcFilesAsync(dbcFiles);
            if (canLogs.Length > 0) await _mainWindow.CanViewModel.LoadLogsAsync(canLogs, canLogs.Length == 1 ? CanLogMergeMode.Replace : CanLogMergeMode.AppendContinuous, 0);
        }
    }

    private static bool IsExcelTemplate(string path) => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".xlsm", StringComparison.OrdinalIgnoreCase);
    private static bool IsElfFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".elf" or ".axf" or ".out";

    private void OpenWaveformButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowWaveformWorkspace();
    }

    private void ReturnButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_showingWaveform) ShowConnectionWorkspace();
        else ReturnToHome();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_showingWaveform) ShowConnectionWorkspace();
            else ReturnToHome();
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (_showingWaveform) ShowConnectionWorkspace();
            else ReturnToHome();
        }
        base.OnClosing(e);
    }
}
