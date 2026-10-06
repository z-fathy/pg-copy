using System.Threading.Channels;
using PgCopy.Core.Catalog;
using PgCopy.Core.Connections;
using PgCopy.Core.Copy;
using PgCopy.Core.Planning;

namespace PgCopy.Core.Orchestration;

/// <summary>Runs the copy work list on N workers, each with its own snapshot-pinned source connection.</summary>
public static class DataCopyStage
{
    public static async Task RunAsync(
        IReadOnlyList<CopyTask> tasks,
        ConnectionProfile source,
        ConnectionProfile targetDatabase,
        string? snapshotId,
        int jobs,
        ProgressTracker tracker,
        Action<TableInfo> onTableCompleted,
        Action<string> warn,
        CancellationToken ct
    )
    {
        if (tasks.Count == 0)
            return;
        var queue = Channel.CreateUnbounded<CopyTask>();
        foreach (var t in tasks)
            queue.Writer.TryWrite(t);
        queue.Writer.Complete();

        var remaining = tasks.GroupBy(t => t.Table.Oid).ToDictionary(g => g.Key, g => new int[] { g.Count() });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task WorkerAsync()
        {
            await using var connections = new WorkerConnections(source, targetDatabase, snapshotId);
            try
            {
                await foreach (var task in queue.Reader.ReadAllAsync(cts.Token))
                {
                    var name = task.Table.DisplayName;
                    tracker.Start(name);
                    await TableCopier.CopyAsync(task, connections, n => tracker.Add(name, n), warn, cts.Token);
                    if (Interlocked.Decrement(ref remaining[task.Table.Oid][0]) == 0)
                    {
                        tracker.Complete(name);
                        onTableCompleted(task.Table);
                    }
                }
            }
            catch
            {
                await cts.CancelAsync();
                throw;
            }
        }

        var workers = Enumerable
            .Range(0, Math.Min(jobs, tasks.Count))
            .Select(_ => Task.Run(WorkerAsync, CancellationToken.None))
            .ToList();
        try
        {
            await Task.WhenAll(workers);
        }
        catch
        {
            await Task.WhenAll(workers.Select(w => w.ContinueWith(_ => { }, TaskScheduler.Default)));
            // Prefer the real failure over the cancellations it triggered in the other workers.
            var real = workers
                .Where(w => w.IsFaulted)
                .SelectMany(w => w.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (real is not null)
                throw real;
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }
}
