using V3RttMonitor.App.Infrastructure;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App.ViewModels;

public sealed class CanWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private CanAnalysisViewModel _activeSession;

    public CanWorkspaceViewModel()
    {
        Online = new CanAnalysisViewModel(CanWorkspaceMode.Online);
        Offline = new CanAnalysisViewModel(CanWorkspaceMode.Offline);
        _activeSession = Online;
    }

    public CanAnalysisViewModel Online { get; }
    public CanAnalysisViewModel Offline { get; }
    public CanAnalysisViewModel ActiveSession
    {
        get => _activeSession;
        private set => SetProperty(ref _activeSession, value);
    }

    public void SelectOnline() => ActiveSession = Online;
    public void SelectOffline() => ActiveSession = Offline;

    public async Task LoadDbcFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var files = paths.ToArray();
        await Task.WhenAll(
            Online.LoadDbcFilesAsync(files, cancellationToken),
            Offline.LoadDbcFilesAsync(files, cancellationToken));
    }

    public void ApplyDatabase(DbcDatabase database, string? path)
    {
        Online.ApplyDatabase(database, path);
        Offline.ApplyDatabase(database, path);
    }

    public void RemoveDbc(string path)
    {
        RemoveFrom(Online, path);
        RemoveFrom(Offline, path);

        static void RemoveFrom(CanAnalysisViewModel session, string selectedPath)
        {
            session.SelectedDbcFile = session.DbcFiles.FirstOrDefault(item => item.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            session.RemoveSelectedDbc();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Online.DisposeAsync();
        await Offline.DisposeAsync();
    }
}
