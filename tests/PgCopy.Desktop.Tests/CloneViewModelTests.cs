using Avalonia.Headless.XUnit;
using Npgsql;
using PgCopy.Core.Connections;
using PgCopy.Desktop.ViewModels;

namespace PgCopy.Desktop.Tests;

/// <summary>Drives the real clone pipeline through the view model. Needs PGCOPY_IT_SOURCE / PGCOPY_IT_TARGET.</summary>
public class CloneViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgcopy-vm-" + Guid.NewGuid().ToString("N")[..8]);

    public CloneViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ConnectionProfile? FromEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { } v ? ConnectionProfile.Parse(v) : null;

    private static async Task ExecAsync(ConnectionProfile server, string database, string sql)
    {
        await using var conn = new NpgsqlConnection(server.ForDatabase(database).ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [AvaloniaFact]
    public async Task ReplicatesSelectedDatabaseAndRecordsHistory()
    {
        var source = FromEnv("PGCOPY_IT_SOURCE");
        var target = FromEnv("PGCOPY_IT_TARGET");
        Assert.SkipUnless(source is not null && target is not null, "Set PGCOPY_IT_SOURCE and PGCOPY_IT_TARGET to run.");

        var db = "uivm_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync(source!, "postgres", $"CREATE DATABASE \"{db}\"");
        try
        {
            await ExecAsync(
                source!,
                db,
                "CREATE TABLE t (id serial PRIMARY KEY, v text); INSERT INTO t (v) SELECT 'row ' || g FROM generate_series(1, 5000) g;"
            );
            var vm = new MainViewModel(AppServices.CreateIsolated(_dir));
            var clone = vm.Clone;
            clone.Source.Host = source!.Host;
            clone.Source.Port = source.Port;
            clone.Source.Username = source.Username;
            clone.Source.Password = source.Password ?? "";
            clone.Target.UseDocker = false;
            clone.Target.Existing.Host = target!.Host;
            clone.Target.Existing.Port = target.Port;
            clone.Target.Existing.Username = target.Username;
            clone.Target.Existing.Password = target.Password ?? "";

            await clone.LoadDatabasesCommand.ExecuteAsync(null);
            var item = Assert.Single(clone.Databases, d => d.Name == db);
            Assert.False(clone.StartCommand.CanExecute(null)); // nothing selected yet
            item.IsSelected = true;
            Assert.True(clone.StartCommand.CanExecute(null));

            await clone.StartCommand.ExecuteAsync(null);

            Assert.Equal("", clone.ErrorMessage);
            Assert.Contains("replicated", clone.StatusMessage);
            Assert.Contains(clone.Tables, t => t.Name == "public.t");
            Assert.True(clone.IsIdle);
            Assert.True(File.Exists(clone.LastLogPath));

            vm.History.Refresh();
            var run = Assert.Single(vm.History.Runs);
            Assert.True(run.Success);
            Assert.Equal(db, run.Title);

            await using var conn = new NpgsqlConnection(target.ForDatabase(db).ToConnectionString(ConnectionRole.Plain));
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("select count(*) from t", conn);
            Assert.Equal(5000L, await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await ExecAsync(source!, "postgres", $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
            await ExecAsync(target!, "postgres", $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
        }
    }

    [AvaloniaFact]
    public async Task ReportsConnectionFailureWithoutThrowing()
    {
        var vm = new MainViewModel(AppServices.CreateIsolated(_dir));
        vm.Clone.Source.Host = "127.0.0.1";
        vm.Clone.Source.Port = 1; // nothing listens here

        await vm.Clone.LoadDatabasesCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not connect", vm.Clone.ErrorMessage);
        Assert.Empty(vm.Clone.Databases);
    }
}
