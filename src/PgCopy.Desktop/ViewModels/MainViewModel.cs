namespace PgCopy.Desktop.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public MainViewModel(AppServices services)
    {
        var target = new TargetSettingsViewModel(services);
        Clone = new CloneViewModel(services, target);
        Snapshots = new SnapshotsViewModel(target);
        History = new HistoryViewModel(services);
        Settings = new SettingsViewModel(services);
        History.Refresh();
    }

    public CloneViewModel Clone { get; }
    public SnapshotsViewModel Snapshots { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
}
