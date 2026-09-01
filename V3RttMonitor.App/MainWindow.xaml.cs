using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using V3RttMonitor.App.ViewModels;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private AcquisitionWindow? _acquisitionWindow;
    private CanAnalysisWindow? _canAnalysisWindow;
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        Loaded += MainWindow_OnLoaded;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        var rawArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var arguments = rawArguments.Where(File.Exists).ToArray();
        var template = arguments.FirstOrDefault(IsExcelTemplate);
        if (template is not null) await _viewModel.LoadTemplateFileAsync(template);
        var elf = arguments.FirstOrDefault(IsElfFile);
        if (elf is not null) await _viewModel.LoadHssElfFileAsync(elf);
        var bin = arguments
            .FirstOrDefault(path => File.Exists(path) && string.Equals(Path.GetExtension(path), ".bin", StringComparison.OrdinalIgnoreCase));
        if (bin is not null) await _viewModel.LoadBinaryFileAsync(bin);
        var dbcFiles = arguments.Where(path => Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase)).ToArray();
        var canLogs = arguments.Where(CanAnalysisWindow.IsCanLog).ToArray();
        if (dbcFiles.Length > 0 || canLogs.Length > 0)
        {
            OpenCanAnalysis();
            _canAnalysisWindow!.ShowOfflineAnalysis();
            if (dbcFiles.Length > 0) await _canAnalysisWindow.Workspace.LoadDbcFilesAsync(dbcFiles);
            if (canLogs.Length > 0) await _canAnalysisWindow!.ViewModel.LoadLogsAsync(canLogs, canLogs.Length == 1 ? CanLogMergeMode.Replace : CanLogMergeMode.AppendContinuous, 0);
        }
        else if (bin is not null) OpenWaveformAnalysis();
        else if (template is not null || elf is not null || rawArguments.Contains("--acquisition", StringComparer.OrdinalIgnoreCase)) OpenAcquisition();
    }

    private async void MainWindow_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var template = files.FirstOrDefault(IsExcelTemplate);
        if (template is not null) await _viewModel.LoadTemplateFileAsync(template);
        var elf = files.FirstOrDefault(IsElfFile);
        if (elf is not null) await _viewModel.LoadHssElfFileAsync(elf);
        var bin = files.FirstOrDefault(path => string.Equals(Path.GetExtension(path), ".bin", StringComparison.OrdinalIgnoreCase));
        if (bin is not null) await _viewModel.LoadBinaryFileAsync(bin);
        var dbcFiles = files.Where(path => Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase)).ToArray();
        var canLogs = files.Where(CanAnalysisWindow.IsCanLog).ToArray();
        if (dbcFiles.Length > 0 || canLogs.Length > 0)
        {
            OpenCanAnalysis();
            _canAnalysisWindow!.ShowOfflineAnalysis();
            if (dbcFiles.Length > 0) await _canAnalysisWindow.Workspace.LoadDbcFilesAsync(dbcFiles);
            if (canLogs.Length > 0) await _canAnalysisWindow!.ViewModel.LoadLogsAsync(canLogs, canLogs.Length == 1 ? CanLogMergeMode.Replace : CanLogMergeMode.AppendContinuous, 0);
        }
        else if (bin is not null) OpenWaveformAnalysis();
        else if (template is not null || elf is not null) OpenAcquisition();
    }

    private static bool IsExcelTemplate(string path) =>
        Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".xlsm", StringComparison.OrdinalIgnoreCase);

    private static bool IsElfFile(string path) =>
        Path.GetExtension(path).Equals(".elf", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".axf", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".out", StringComparison.OrdinalIgnoreCase);

    private void OpenAcquisitionButton_OnClick(object sender, RoutedEventArgs e) => OpenAcquisition();

    private void OpenCanAnalysisButton_OnClick(object sender, RoutedEventArgs e) => OpenCanAnalysis();

    private void OpenDbcEditorButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new DbcEditorWindow(_canAnalysisWindow?.ViewModel.Database, null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SavedDatabase is not null)
        {
            _canAnalysisWindow ??= new CanAnalysisWindow(this);
            _canAnalysisWindow.Workspace.ApplyDatabase(dialog.SavedDatabase, dialog.SavedPath);
        }
    }

    public CanAnalysisViewModel CanViewModel => _canAnalysisWindow?.ViewModel
        ?? throw new InvalidOperationException("CAN工作台尚未创建。");

    public void ShowHome()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    public void OpenAcquisition()
    {
        if (_shutdownInProgress || _shutdownCompleted) return;
        _acquisitionWindow ??= new AcquisitionWindow(_viewModel, this);
        Hide();
        _acquisitionWindow.ShowWorkspace();
    }

    public void OpenWaveformAnalysis()
    {
        if (_shutdownInProgress || _shutdownCompleted) return;

        _acquisitionWindow ??= new AcquisitionWindow(_viewModel, this);
        Hide();
        _acquisitionWindow.ShowWaveformWorkspace();
    }

    public void OpenCanAnalysis()
    {
        if (_shutdownInProgress || _shutdownCompleted) return;
        _canAnalysisWindow ??= new CanAnalysisWindow(this);
        Hide();
        _canAnalysisWindow.ShowAnalysis();
    }

    public void OpenCanOfflineAnalysis()
    {
        OpenCanAnalysis();
        _canAnalysisWindow?.ShowOfflineAnalysis();
    }

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D2))
        {
            OpenWaveformAnalysis();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D1)
        {
            OpenAcquisition();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D3)
        {
            OpenCanAnalysis();
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownCompleted)
        {
            e.Cancel = true;
            if (!_shutdownInProgress)
            {
                var confirmation = new ExitConfirmationWindow { Owner = this };
                if (confirmation.ShowDialog() == true) _ = ShutdownAsync();
            }
        }
        base.OnClosing(e);
    }

    private async Task ShutdownAsync()
    {
        _shutdownInProgress = true;
        Cursor = Cursors.Wait;
        Title = "JustFloat Studio · 正在释放采集与网络资源…";
        ClosingStatusText.Text = "正在停止RTT、HSS、CAN接收，写完BIN并释放J-Link进程…";
        ClosingOverlay.Visibility = Visibility.Visible;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        try
        {
            if (_canAnalysisWindow is not null)
            {
                try { await _canAnalysisWindow.ForceCloseAsync(); }
                catch (Exception ex) { Debug.WriteLine($"关闭CAN分析资源时发生异常：{ex}"); }
            }
            try { await _viewModel.DisposeAsync(); }
            catch (Exception ex) { Debug.WriteLine($"关闭RTT/HSS资源时发生异常：{ex}"); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"关闭资源时发生异常：{ex}");
        }
        finally
        {
            ClosingStatusText.Text = "资源已释放，正在退出…";
            _acquisitionWindow?.ForceClose();
            _shutdownCompleted = true;
            _shutdownInProgress = false;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            await Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
        }
    }
}
