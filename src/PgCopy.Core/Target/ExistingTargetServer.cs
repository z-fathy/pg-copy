using Npgsql;
using PgCopy.Core.Connections;

namespace PgCopy.Core.Target;

/// <summary>An already-running local server. Its configuration is never modified.</summary>
public sealed class ExistingTargetServer : ITargetServer
{
    public ExistingTargetServer(ConnectionProfile admin) => Admin = admin;

    public string Name => $"existing server {Admin.Host}:{Admin.Port}";

    public ConnectionProfile Admin { get; }

    public string? DockerContainer => null;

    public long MemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public int CpuCount => Environment.ProcessorCount;

    public async Task PrepareAsync(int sourceMajor, Action<string> log, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Admin.ToConnectionString(ConnectionRole.Plain));
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException($"Cannot connect to the target server {Admin}: {ex.Message}", ex);
        }
        var major = int.Parse((await DatabaseAdmin.ScalarAsync<string>(conn, "show server_version_num", ct))!) / 10000;
        if (major < sourceMajor)
            throw new InvalidOperationException(
                $"The target server is PostgreSQL {major} but the source is {sourceMajor}; the target must be the same or newer."
            );
        var isSuper = await DatabaseAdmin.ScalarAsync<bool>(
            conn,
            "select rolsuper from pg_roles where rolname = current_user",
            ct
        );
        if (!isSuper)
            log("The target user is not a superuser: extensions needing superuser and trusted foreign keys will be limited.");
    }

    public Task EnterLoadModeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ExitLoadModeAsync(CancellationToken ct) => Task.CompletedTask;
}
