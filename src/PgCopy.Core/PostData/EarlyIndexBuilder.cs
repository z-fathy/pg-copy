using System.Threading.Channels;
using Npgsql;
using PgCopy.Core.Catalog;
using PgCopy.Core.Connections;
using PgCopy.Core.Target;

namespace PgCopy.Core.PostData;

/// <summary>
/// A pool of target sessions that builds indexes and constraints while other tables are still copying.
/// Each index is its own work item so one big table's indexes build in parallel; the table is ANALYZEd once
/// its last index completes. This is an optimisation only: an index that fails here is reported and left to
/// the post-data restore, which will either build it or surface the real error.
/// </summary>
public sealed class EarlyIndexBuilder
{
    private sealed class Countdown(int count)
    {
        public int Remaining = count;
    }

    private readonly record struct Item(string TableDisplay, string TableQuoted, IndexDefinition? Index, Countdown? Left);

    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>();
    private readonly List<Task> _workers = [];
    private readonly ConnectionProfile _target;
    private readonly int _maintenanceWorkMemMb;
    private readonly Action<IndexDefinition> _onBuilt;
    private readonly Action<string> _onFailed;
    private readonly Action<string> _onAnalyzed;
    private int _done;

    public EarlyIndexBuilder(
        ConnectionProfile targetDatabase,
        int workers,
        int maintenanceWorkMemMb,
        Action<IndexDefinition> onBuilt,
        Action<string> onFailed,
        Action<string> onAnalyzed,
        CancellationToken ct
    )
    {
        _target = targetDatabase;
        _maintenanceWorkMemMb = maintenanceWorkMemMb;
        _onBuilt = onBuilt;
        _onFailed = onFailed;
        _onAnalyzed = onAnalyzed;
        for (var i = 0; i < workers; i++)
            _workers.Add(Task.Run(() => RunWorkerAsync(ct), ct));
    }

    /// <summary>Indexes finished (successfully or not).</summary>
    public int Completed => Volatile.Read(ref _done);

    /// <summary>Queues a finished table: its indexes (if any), then an ANALYZE.</summary>
    public void Enqueue(string tableDisplay, string tableQuoted, IReadOnlyList<IndexDefinition> indexes)
    {
        if (indexes.Count == 0)
        {
            _queue.Writer.TryWrite(new Item(tableDisplay, tableQuoted, null, null));
            return;
        }
        var left = new Countdown(indexes.Count);
        foreach (var index in indexes)
            _queue.Writer.TryWrite(new Item(tableDisplay, tableQuoted, index, left));
    }

    public async Task CompleteAsync()
    {
        _queue.Writer.Complete();
        await Task.WhenAll(_workers);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(
            _target.ToConnectionString(ConnectionRole.TargetMaintenance, _maintenanceWorkMemMb)
        );
        await conn.OpenAsync(ct);
        await DatabaseAdmin.ExecAsync(conn, "SET search_path = ''", ct);
        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            if (item.Index is { } index)
            {
                try
                {
                    await DatabaseAdmin.ExecAsync(conn, index.Sql, ct);
                    _onBuilt(index);
                }
                catch (PostgresException ex)
                {
                    _onFailed(
                        $"{item.TableDisplay}: could not build {index.Name} early ({ex.MessageText}); deferring to post-data restore."
                    );
                }
                Interlocked.Increment(ref _done);
            }

            if (item.Left is null || Interlocked.Decrement(ref item.Left.Remaining) == 0)
            {
                try
                {
                    await DatabaseAdmin.ExecAsync(conn, $"ANALYZE {item.TableQuoted}", ct);
                    _onAnalyzed(item.TableQuoted);
                }
                catch (PostgresException ex)
                {
                    _onFailed($"{item.TableDisplay}: analyze failed ({ex.MessageText}).");
                }
            }
        }
    }
}
