using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Npgsql;
using PgCopy.Core;
using PgCopy.Core.Connections;
using PgCopy.Core.Snapshots;
using PgCopy.Core.Target;

namespace PgCopy.Desktop.ViewModels;

/// <summary>Instant local save points (template-database copies) of cloned databases.</summary>
public sealed partial class SnapshotsViewModel(TargetSettingsViewModel target) : ViewModelBase
{
    public TargetSettingsViewModel Target { get; } = target;
    public ObservableCollection<string> LocalDatabases { get; } = [];
    public ObservableCollection<SnapshotRow> Snapshots { get; } = [];

    [ObservableProperty]
    private string? _selectedDatabase;

    [ObservableProperty]
    private string _snapshotName = "fresh";

    [ObservableProperty]
    private SnapshotRow? _selectedSnapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _message = "Refresh to list the databases on the target.";

    [ObservableProperty]
    private string _errorMessage = "";

    private async Task<(LocalSnapshotService Service, NpgsqlConnection Connection)> ConnectAsync()
    {
        var server = Target.CreateServer();
        await server.PrepareAsync(Target.Major, _ => { }, CancellationToken.None);
        var conn = new NpgsqlConnection(server.Admin.ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync();
        return (new LocalSnapshotService(server.Admin), conn);
    }

    private async Task RunAsync(Func<LocalSnapshotService, Task> action, string done)
    {
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            var (service, conn) = await ConnectAsync();
            await using (conn)
                await action(service);
            Message = done;
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            await ReloadAsync();
            Message = $"{LocalDatabases.Count} local database(s), {Snapshots.Count} snapshot(s).";
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAsync()
    {
        var server = Target.CreateServer();
        await server.PrepareAsync(Target.Major, _ => { }, CancellationToken.None);
        await using var conn = new NpgsqlConnection(server.Admin.ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync();
        var names = new List<string>();
        await using (
            var cmd = new NpgsqlCommand(
                "select datname from pg_database where not datistemplate and datname <> 'postgres' and datname not like '%\\_\\_snap\\_%' and datname not like '%\\_\\_pgcopy\\_%' order by 1",
                conn
            )
        )
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
                names.Add(r.GetString(0));
        }
        LocalDatabases.Clear();
        foreach (var n in names)
            LocalDatabases.Add(n);
        Snapshots.Clear();
        foreach (var s in await new LocalSnapshotService(server.Admin).ListAsync(null, CancellationToken.None))
            Snapshots.Add(new SnapshotRow(s.Database, s.Name, ByteSize.Format(s.SizeBytes)));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task SaveAsync() =>
        SelectedDatabase is { } db && !string.IsNullOrWhiteSpace(SnapshotName)
            ? RunAsync(s => s.SaveAsync(db, SnapshotName.Trim(), default), $"Saved snapshot '{SnapshotName.Trim()}' of {db}.")
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RestoreAsync() =>
        SelectedSnapshot is { } snap
            ? RunAsync(
                s => s.RestoreAsync(snap.Database, snap.Name, default),
                $"{snap.Database} reset to snapshot '{snap.Name}'."
            )
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DeleteAsync() =>
        SelectedSnapshot is { } snap
            ? RunAsync(s => s.DeleteAsync(snap.Database, snap.Name, default), $"Deleted snapshot '{snap.Name}'.")
            : Task.CompletedTask;
}

public sealed record SnapshotRow(string Database, string Name, string Size)
{
    public string Title => $"{Database} · {Name}";
}
