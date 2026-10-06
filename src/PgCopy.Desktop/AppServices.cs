using PgCopy.Core;
using PgCopy.Core.Connections;
using PgCopy.Core.Orchestration;

namespace PgCopy.Desktop;

/// <summary>The persisted stores shared by all view models.</summary>
public sealed record AppServices(
    ISecretStore Secrets,
    ProfileStore Profiles,
    AppSettingsStore SettingsStore,
    RunHistory History
)
{
    public static AppServices CreateDefault()
    {
        var secrets = new FileSecretStore();
        return new AppServices(secrets, new ProfileStore(secrets), new AppSettingsStore(), new RunHistory());
    }

    /// <summary>Isolated stores under a temp directory, for tests.</summary>
    public static AppServices CreateIsolated(string directory)
    {
        var secrets = new FileSecretStore(Path.Combine(directory, "secrets.json"));
        return new AppServices(
            secrets,
            new ProfileStore(secrets, Path.Combine(directory, "profiles.json")),
            new AppSettingsStore(Path.Combine(directory, "settings.json")),
            new RunHistory(Path.Combine(directory, "history.jsonl"))
        );
    }
}
