using Npgsql;
using PgCopy.Core.Connections;
using PgCopy.Core.Target;

namespace PgCopy.Core.Snapshots;

public sealed record LocalSnapshot(string Database, string Name, long SizeBytes);

/// <summary>
/// Instant local save points: a snapshot is a template-database copy (<c>db__snap_name</c>), so resetting a
/// dev database to its freshly cloned state takes seconds instead of a re-download.
/// </summary>
public sealed class LocalSnapshotService
{
    private const string Marker = "__snap_";
    private readonly ConnectionProfile _admin;

    public LocalSnapshotService(ConnectionProfile admin) => _admin = admin;

    public static string SnapshotDatabaseName(string database, string name) => $"{database}{Marker}{name}";

    public async Task SaveAsync(string database, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var snap = SnapshotDatabaseName(database, name);
        await DatabaseAdmin.DropIfExistsAsync(conn, snap, ct);
        await CopyAsync(conn, database, snap, ct);
    }

    public async Task RestoreAsync(string database, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var snap = SnapshotDatabaseName(database, name);
        if (!await DatabaseAdmin.ExistsAsync(conn, snap, ct))
            throw new InvalidOperationException($"No snapshot '{name}' exists for database '{database}'.");
        var staging = database + "__pgcopy_tmp";
        await DatabaseAdmin.DropIfExistsAsync(conn, staging, ct);
        await CopyAsync(conn, snap, staging, ct);
        await DatabaseAdmin.SwapAsync(conn, staging, database, ct);
    }

    public async Task DeleteAsync(string database, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await DatabaseAdmin.DropIfExistsAsync(conn, SnapshotDatabaseName(database, name), ct);
    }

    public async Task<IReadOnlyList<LocalSnapshot>> ListAsync(string? database, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var result = new List<LocalSnapshot>();
        await using var cmd = new NpgsqlCommand(
            "select datname, pg_database_size(oid) from pg_database where datname like '%\\_\\_snap\\_%' order by 1",
            conn
        );
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var full = r.GetString(0);
            var idx = full.IndexOf(Marker, StringComparison.Ordinal);
            var db = full[..idx];
            if (database is null || db == database)
                result.Add(new LocalSnapshot(db, full[(idx + Marker.Length)..], r.GetInt64(1)));
        }
        return result;
    }

    private static async Task CopyAsync(NpgsqlConnection conn, string from, string to, CancellationToken ct)
    {
        // A template database must have no other sessions.
        await DatabaseAdmin.TerminateConnectionsAsync(conn, from, ct);
        var version = int.Parse((await DatabaseAdmin.ScalarAsync<string>(conn, "show server_version_num", ct))!);
        if (version >= 180000)
        {
            // Reflink-capable filesystems make this near-instant; fall back to a plain copy otherwise.
            try
            {
                await DatabaseAdmin.ExecAsync(conn, "SET file_copy_method = clone", ct);
            }
            catch (PostgresException) { }
        }
        var strategy = version >= 150000 ? " STRATEGY FILE_COPY" : "";
        await DatabaseAdmin.ExecAsync(
            conn,
            $"CREATE DATABASE {DatabaseAdmin.Ident(to)} TEMPLATE {DatabaseAdmin.Ident(from)}{strategy}",
            ct
        );
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_admin.ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync(ct);
        return conn;
    }
}
