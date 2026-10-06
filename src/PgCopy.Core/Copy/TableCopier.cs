using Npgsql;
using PgCopy.Core.Planning;

namespace PgCopy.Core.Copy;

public sealed record CopyOutcome(long Bytes, bool UsedBinary, int Attempts);

/// <summary>Copies one <see cref="CopyTask"/>: builds the COPY statements, applies the load tricks and retries.</summary>
public static class TableCopier
{
    private const int MaxAttempts = 3;

    public static async Task<CopyOutcome> CopyAsync(
        CopyTask task,
        WorkerConnections connections,
        Action<long> onBytes,
        Action<string> onWarning,
        CancellationToken ct
    )
    {
        var binary = task.Binary;
        var allowFreeze = true;
        for (var attempt = 1; ; attempt++)
        {
            long reported = 0;
            void Count(long n)
            {
                reported += n;
                onBytes(n);
            }

            try
            {
                var bytes = await CopyOnceAsync(task, connections, binary, allowFreeze, Count, ct);
                return new CopyOutcome(bytes, binary, attempt);
            }
            catch (OperationCanceledException)
            {
                await connections.InvalidateAsync();
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                await connections.InvalidateAsync();
                onBytes(-reported); // Roll the progress counter back; the target transaction was rolled back too.
                if (ex is PostgresException { SqlState: "55000" } && allowFreeze)
                {
                    allowFreeze = false;
                    onWarning($"{task.Label}: COPY FREEZE not allowed ({ex.Message}); retrying without it.");
                }
                else if (binary && ex is PostgresException)
                {
                    binary = false;
                    onWarning($"{task.Label}: binary COPY failed ({ex.Message}); retrying as text.");
                }
                else
                {
                    onWarning($"{task.Label}: attempt {attempt} failed ({ex.Message}); retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                }
            }
            catch
            {
                await connections.InvalidateAsync();
                throw;
            }
        }
    }

    private static async Task<long> CopyOnceAsync(
        CopyTask task,
        WorkerConnections connections,
        bool binary,
        bool allowFreeze,
        Action<long> onBytes,
        CancellationToken ct
    )
    {
        var source = await connections.SourceAsync(ct);
        var target = await connections.TargetAsync(ct);
        var export = BuildExport(task, binary);

        // A whole table is truncated and loaded in a single transaction so COPY FREEZE is allowed and, with
        // wal_level=minimal, writes no WAL. Chunks of a split table (or extension config tables that already
        // hold rows) load with a plain COPY.
        var canTruncate = task.IsWholeTable && !task.Table.IsExtensionConfigTable;
        var canFreeze = canTruncate && allowFreeze;
        await Exec(target, "BEGIN", ct);
        try
        {
            if (canTruncate)
                await Exec(target, $"TRUNCATE ONLY {task.Table.QualifiedName}", ct);
            var import = BuildImport(task, binary, canFreeze);
            var bytes = binary
                ? await CopyRelay.RelayBinaryAsync(source, export, target, import, onBytes, ct)
                : await CopyRelay.RelayTextAsync(source, export, target, import, onBytes, ct);
            await Exec(target, "COMMIT", ct);
            return bytes;
        }
        catch
        {
            try
            {
                await Exec(target, "ROLLBACK", CancellationToken.None);
            }
            catch
            {
                // Connection is being discarded by the caller.
            }
            throw;
        }
    }

    public static string BuildExport(CopyTask task, bool binary)
    {
        var format = binary ? "FORMAT binary" : "FORMAT text";
        var table = task.Table;
        if (task.IsWholeTable && table.ExtensionCondition is null)
            return $"COPY {table.QualifiedName} ({table.ColumnList}) TO STDOUT ({format})";

        var where = new List<string>();
        if (table.ExtensionCondition is not null)
            where.Add($"({table.ExtensionCondition})");
        if (task.StartPage is { } start)
            where.Add($"ctid >= '({start},0)'::tid");
        if (task.EndPage is { } end)
            where.Add($"ctid < '({end},0)'::tid");
        var filter = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        return $"COPY (SELECT {table.ColumnList} FROM ONLY {table.QualifiedName}{filter}) TO STDOUT ({format})";
    }

    public static string BuildImport(CopyTask task, bool binary, bool freeze)
    {
        var options = new List<string> { binary ? "FORMAT binary" : "FORMAT text" };
        if (freeze)
            options.Add("FREEZE");
        return $"COPY {task.Table.QualifiedName} ({task.Table.ColumnList}) FROM STDIN ({string.Join(", ", options)})";
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
