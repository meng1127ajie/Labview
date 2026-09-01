using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using V3RttMonitor.App.ViewModels;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App;

public partial class CanAnalysisWindow : Window
{
    private readonly MainWindow _mainWindow;
    private bool _allowClose;

    public CanAnalysisWindow(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        Workspace = new CanWorkspaceViewModel();
        DataContext = Workspace;
    }

    public CanWorkspaceViewModel Workspace { get; }
    public CanAnalysisViewModel ViewModel => Workspace.Offline;
    public CanAnalysisViewModel OnlineViewModel => Workspace.Online;

    public void ShowAnalysis()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Activate();
        Focus();
    }

    public void ShowOfflineAnalysis()
    {
        CanModeTabs.SelectedIndex = 1;
        Workspace.SelectOffline();
        ShowAnalysis();
    }

    public void ReturnToMain()
    {
        if (_allowClose) return;
        Hide();
        _mainWindow.ShowHome();
    }

    public async Task ForceCloseAsync()
    {
        _allowClose = true;
        await Workspace.DisposeAsync();
        Close();
    }

    private void ReturnButton_OnClick(object sender, RoutedEventArgs e) => ReturnToMain();

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var dbcFiles = files.Where(path => Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (dbcFiles.Length > 0) await Workspace.LoadDbcFilesAsync(dbcFiles);
        var logs = files.Where(IsCanLog).ToArray();
        if (logs.Length == 0) return;
        CanModeTabs.SelectedIndex = 1;
        Workspace.SelectOffline();
        var mode = ViewModel.HistoryFrameCount == 0 && logs.Length == 1 ? CanLogMergeMode.Replace : CanLogMergeMode.AppendContinuous;
        if (mode != CanLogMergeMode.Replace)
        {
            var mergeDialog = new CanLogMergeWindow(ViewModel.HistoryFrameCount, logs.Length) { Owner = this };
            if (mergeDialog.ShowDialog() != true) return;
            mode = mergeDialog.MergeMode;
            await ViewModel.LoadLogsAsync(logs, mode, mergeDialog.GapSeconds);
        }
        else await ViewModel.LoadLogsAsync(logs, mode, 0);
    }

    public static bool IsCanLog(string path) => Path.GetExtension(path).ToLowerInvariant() is ".blf" or ".asc" or ".log" or ".txt" or ".csv";

    private void CanModeTabs_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CanModeTabs)) return;
        if (DataContext is not CanWorkspaceViewModel workspace) return;
        if (CanModeTabs.SelectedIndex == 1) workspace.SelectOffline();
        else workspace.SelectOnline();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ReturnToMain();
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
            ReturnToMain();
        }
        base.OnClosing(e);
    }
}
