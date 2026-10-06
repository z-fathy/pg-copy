using CommunityToolkit.Mvvm.ComponentModel;
using PgCopy.Core;
using PgCopy.Core.Target;

namespace PgCopy.Desktop.ViewModels;

/// <summary>Where clones land: the tool-managed Docker container, or a server the user already runs.</summary>
public sealed partial class TargetSettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public TargetSettingsViewModel(AppServices services)
    {
        _services = services;
        var s = services.SettingsStore.Load();
        UseDocker = s.TargetMode != "Existing";
        Existing.Host = s.ExistingHost;
        Existing.Port = s.ExistingPort;
        Existing.Username = s.ExistingUser;
        Existing.SslMode = "Prefer";
        DockerMajor = s.DockerMajor;
    }

    public ConnectionFormViewModel Existing { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseExisting))]
    private bool _useDocker = true;

    public bool UseExisting => !UseDocker;

    [ObservableProperty]
    private decimal? _dockerMajor = 16;

    [ObservableProperty]
    private string _dockerStatus = "";

    public ITargetServer CreateServer(bool safeLoad = false)
    {
        var settings = _services.SettingsStore.Load();
        return UseDocker
            ? new DockerTargetServer(_services.Secrets, settings.DockerImage, safeLoad)
            : new ExistingTargetServer(Existing.ToProfile());
    }

    public int Major => (int)(DockerMajor ?? 16);

    public string Describe() =>
        UseDocker
            ? $"Managed Docker (PostgreSQL {Major})"
            : $"{Existing.Username}@{Existing.Host}:{(int)(Existing.Port ?? 5432)}";

    public void Persist()
    {
        var s = _services.SettingsStore.Load();
        _services.SettingsStore.Save(
            s with
            {
                TargetMode = UseDocker ? "Docker" : "Existing",
                ExistingHost = Existing.Host,
                ExistingPort = (int)(Existing.Port ?? 5432),
                ExistingUser = Existing.Username,
                DockerMajor = Major,
            }
        );
    }

    public async Task RefreshDockerStatusAsync(CancellationToken ct = default) =>
        DockerStatus = UseDocker ? "Container: " + await DockerTargetServer.StatusAsync(Major, ct) : "";
}
