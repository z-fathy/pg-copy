using Npgsql;
using PgCopy.Core.Connections;

namespace PgCopy.Core.Copy;

/// <summary>
/// The long-lived source and target connections owned by one copy worker. The source connection sits in a
/// REPEATABLE READ transaction pinned to the shared snapshot. After any failure the connections are
/// discarded and transparently reopened (and the snapshot re-imported) on next use.
/// </summary>
public sealed class WorkerConnections : IAsyncDisposable
{
    private readonly ConnectionProfile _source;
    private readonly ConnectionProfile _target;
    private readonly string? _snapshotId;
    private NpgsqlConnection? _sourceConn;
    private NpgsqlConnection? _targetConn;

    public WorkerConnections(ConnectionProfile source, ConnectionProfile target, string? snapshotId)
    {
        _source = source;
        _target = target;
        _snapshotId = snapshotId;
    }

    public async Task<NpgsqlConnection> SourceAsync(CancellationToken ct)
    {
        if (_sourceConn is { State: System.Data.ConnectionState.Open })
            return _sourceConn;
        await DisposeQuietly(_sourceConn);
        var conn = new NpgsqlConnection(_source.ToConnectionString(ConnectionRole.SourceStream));
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY", ct);
        if (_snapshotId is not null)
            await ExecAsync(conn, $"SET TRANSACTION SNAPSHOT '{_snapshotId.Replace("'", "''")}'", ct);
        return _sourceConn = conn;
    }

    public async Task<NpgsqlConnection> TargetAsync(CancellationToken ct)
    {
        if (_targetConn is { State: System.Data.ConnectionState.Open })
            return _targetConn;
        await DisposeQuietly(_targetConn);
        var conn = new NpgsqlConnection(_target.ToConnectionString(ConnectionRole.TargetStream));
        await conn.OpenAsync(ct);
        return _targetConn = conn;
    }

    /// <summary>Discards both connections; call after any failure since a mid-COPY error leaves them unusable.</summary>
    public async Task InvalidateAsync()
    {
        await DisposeQuietly(_sourceConn);
        await DisposeQuietly(_targetConn);
        _sourceConn = _targetConn = null;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DisposeQuietly(NpgsqlConnection? conn)
    {
        if (conn is null)
            return;
        try
        {
            await conn.DisposeAsync();
        }
        catch
        {
            // Connection is already broken; nothing to clean up.
        }
    }

    public async ValueTask DisposeAsync() => await InvalidateAsync();
}
