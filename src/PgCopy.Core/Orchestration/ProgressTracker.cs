using System.Collections.Concurrent;
using System.Diagnostics;

namespace PgCopy.Core.Orchestration;

/// <summary>Thread-safe byte counters plus a ticker that publishes throttled <see cref="ProgressEvent"/>s.</summary>
public sealed class ProgressTracker
{
    private sealed class Counter
    {
        public long Bytes;
        public long Reported = -1;
        public int State;
    }

    private readonly ConcurrentDictionary<string, Counter> _tables = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _total;
    private int _tablesDone;
    private int _tablesTotal;
    private Func<int> _indexesDone = () => 0;
    private readonly Queue<(double Seconds, long Bytes)> _window = new();

    public double PeakMBps { get; private set; }

    public long TotalBytes => Interlocked.Read(ref _total);

    public double AverageMBps => TotalBytes / 1048576.0 / Math.Max(_clock.Elapsed.TotalSeconds, 0.001);

    public void Register(IEnumerable<string> tables)
    {
        foreach (var t in tables)
            _tables.TryAdd(t, new Counter());
        _tablesTotal = _tables.Count;
    }

    public void BindIndexCounter(Func<int> indexesDone) => _indexesDone = indexesDone;

    public void Add(string table, long bytes)
    {
        Interlocked.Add(ref _total, bytes);
        if (_tables.TryGetValue(table, out var c))
            Interlocked.Add(ref c.Bytes, bytes);
    }

    public void Start(string table)
    {
        if (_tables.TryGetValue(table, out var c) && c.State == (int)TableState.Pending)
            c.State = (int)TableState.Copying;
    }

    public void Complete(string table)
    {
        if (_tables.TryGetValue(table, out var c))
            c.State = (int)TableState.Done;
        Interlocked.Increment(ref _tablesDone);
    }

    public async Task RunTickerAsync(IProgress<CloneEvent> progress, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                Publish(progress);
        }
        catch (OperationCanceledException) { }
        Publish(progress);
    }

    private void Publish(IProgress<CloneEvent> progress)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var total = TotalBytes;
        _window.Enqueue((now, total));
        while (_window.Count > 1 && now - _window.Peek().Seconds > 2)
            _window.Dequeue();
        var oldest = _window.Peek();
        var mbps = now - oldest.Seconds > 0.2 ? (total - oldest.Bytes) / 1048576.0 / (now - oldest.Seconds) : 0;
        PeakMBps = Math.Max(PeakMBps, mbps);

        var changed = new List<TableProgress>();
        foreach (var (name, c) in _tables)
        {
            var bytes = Interlocked.Read(ref c.Bytes);
            var state = (TableState)c.State;
            var marker = bytes * 4 + c.State;
            if (marker != c.Reported)
            {
                c.Reported = marker;
                changed.Add(new TableProgress(name, bytes, state));
            }
        }
        progress.Report(new ProgressEvent(total, mbps, _tablesDone, _tablesTotal, _indexesDone(), changed));
    }
}
