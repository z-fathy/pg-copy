using Npgsql;

namespace PgCopy.Core.Target;

/// <summary>Small helpers for server-level DDL (create/rename/drop databases, kill sessions).</summary>
public static class DatabaseAdmin
{
    public static string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T?> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public static async Task<bool> ExistsAsync(NpgsqlConnection admin, string database, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select 1 from pg_database where datname = $1", admin);
        cmd.Parameters.AddWithValue(database);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public static Task TerminateConnectionsAsync(NpgsqlConnection admin, string database, CancellationToken ct) =>
        ExecAsync(
            admin,
            $"select pg_terminate_backend(pid) from pg_stat_activity where datname = {Literal(database)} and pid <> pg_backend_pid()",
            ct
        );

    public static async Task DropIfExistsAsync(NpgsqlConnection admin, string database, CancellationToken ct)
    {
        if (!await ExistsAsync(admin, database, ct))
            return;
        await TerminateConnectionsAsync(admin, database, ct);
        await ExecAsync(admin, $"DROP DATABASE IF EXISTS {Ident(database)} WITH (FORCE)", ct);
    }

    /// <summary>
    /// Replaces <paramref name="final"/> with <paramref name="staged"/>: the old database stays fully usable
    /// until the new one is complete, then both renames happen back to back.
    /// </summary>
    public static async Task SwapAsync(NpgsqlConnection admin, string staged, string final, CancellationToken ct)
    {
        var old = final + "__pgcopy_old";
        await DropIfExistsAsync(admin, old, ct);
        var hadOld = await ExistsAsync(admin, final, ct);
        if (hadOld)
        {
            await TerminateConnectionsAsync(admin, final, ct);
            await ExecAsync(admin, $"ALTER DATABASE {Ident(final)} RENAME TO {Ident(old)}", ct);
        }
        await TerminateConnectionsAsync(admin, staged, ct);
        await ExecAsync(admin, $"ALTER DATABASE {Ident(staged)} RENAME TO {Ident(final)}", ct);
        if (hadOld)
            await DropIfExistsAsync(admin, old, ct);
    }
}
