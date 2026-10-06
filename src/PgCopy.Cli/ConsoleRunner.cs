using System.Diagnostics;
using Npgsql;
using PgCopy.Core;
using PgCopy.Core.Orchestration;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PgCopy.Cli;

/// <summary>Runs one clone and renders progress: a live dashboard on a terminal, plain lines when redirected.</summary>
internal static class ConsoleRunner
{
    public static async Task<int> RunAsync(CloneRequest request, CancellationToken ct)
    {
        if (Console.IsOutputRedirected)
            AnsiConsole.Profile.Width = 240;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AnsiConsole.MarkupLine(
            $"[bold]Cloning[/] {Markup.Escape(request.SourceDatabase)} -> {Markup.Escape(request.TargetDatabase)}"
        );
        var state = new DashboardState();
        var progress = new Progress<CloneEvent>(state.Apply);
        CloneReport? report = null;
        Exception? failure = null;

        async Task Work()
        {
            try
            {
                report = await new CloneJob().RunAsync(request, progress, cts.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (Console.IsOutputRedirected)
        {
            state.Plain = true;
            await Work();
        }
        else
        {
            await AnsiConsole
                .Live(state.Render())
                .AutoClear(false)
                .StartAsync(async live =>
                {
                    var work = Work();
                    while (!work.IsCompleted)
                    {
                        live.UpdateTarget(state.Render());
                        await Task.WhenAny(work, Task.Delay(200));
                    }
                    live.UpdateTarget(state.Render());
                });
        }

        if (failure is OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/] The previous local copy (if any) was left untouched.");
            return 130;
        }
        if (failure is not null)
        {
            AnsiConsole.MarkupLine(
                $"[red]Failed:[/] {Markup.Escape(failure is PostgresException pg ? pg.MessageText : failure.Message)}"
            );
            if (Environment.GetEnvironmentVariable("PGCOPY_DEBUG") is not null)
                AnsiConsole.WriteException(failure);
            return 1;
        }
        PrintReport(report!);
        return 0;
    }

    private static void PrintReport(CloneReport r)
    {
        var table = new Table()
            .Title("[green]Clone complete[/]")
            .AddColumn("Phase")
            .AddColumn(new TableColumn("Time").RightAligned());
        foreach (var (phase, time) in r.PhaseTimes.OrderBy(p => p.Key))
            table.AddRow(phase.ToString(), $"{time.TotalSeconds:0.0}s");
        table.AddRow("[bold]Total[/]", $"[bold]{r.Total.TotalSeconds:0.0}s[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"{r.Tables} tables, {r.Indexes} indexes built early, {ByteSize.Format(r.BytesTransferred)} streamed "
                + $"(avg {r.AverageMBps:0.0} MB/s, peak {r.PeakMBps:0.0} MB/s)."
        );
        foreach (var w in r.Warnings.Take(20))
            AnsiConsole.MarkupLine($"[yellow]warning:[/] {Markup.Escape(w)}");
    }

    private sealed class DashboardState
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TableProgress> _active = [];
        private readonly Dictionary<string, long> _estimates = [];
        private readonly List<string> _log = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private ClonePhase _phase;
        private ProgressEvent? _last;
        private long _lastPlainTick;

        public bool Plain { get; set; }

        public void Apply(CloneEvent e)
        {
            lock (_gate)
            {
                switch (e)
                {
                    case PhaseEvent p:
                        _phase = p.Phase;
                        if (Plain)
                            Console.WriteLine($"[{_clock.Elapsed.TotalSeconds, 6:0.0}s] {p.Phase}");
                        break;
                    case LogEvent l:
                        _log.Add((l.IsWarning ? "[yellow]warning:[/] " : "") + Markup.Escape(l.Message));
                        if (_log.Count > 6)
                            _log.RemoveAt(0);
                        if (Plain)
                            Console.WriteLine($"{(l.IsWarning ? "warning: " : "")}{l.Message}");
                        break;
                    case PlanEvent plan:
                        foreach (var t in plan.Tables)
                            _estimates[t.Name] = t.EstimatedBytes;
                        break;
                    case ProgressEvent pr:
                        _last = pr;
                        foreach (var t in pr.Changed)
                        {
                            if (t.State == TableState.Copying)
                                _active[t.Name] = t;
                            else
                                _active.Remove(t.Name);
                        }
                        if (Plain && Environment.TickCount64 - _lastPlainTick > 5000)
                        {
                            _lastPlainTick = Environment.TickCount64;
                            Console.WriteLine(
                                $"  {ByteSize.Format(pr.BytesTransferred)} ({pr.CurrentMBps:0.0} MB/s), tables {pr.TablesDone}/{pr.TablesTotal}, indexes {pr.IndexesDone}"
                            );
                        }
                        break;
                }
            }
        }

        public IRenderable Render()
        {
            lock (_gate)
            {
                var grid = new Grid().AddColumn();
                var header = $"[bold]{_phase}[/]  {_clock.Elapsed.TotalSeconds:0}s";
                if (_last is { } p)
                    header +=
                        $"   {ByteSize.Format(p.BytesTransferred)} streamed  [green]{p.CurrentMBps:0.0} MB/s[/]   tables {p.TablesDone}/{p.TablesTotal}   indexes {p.IndexesDone}";
                grid.AddRow(new Markup(header));
                foreach (var t in _active.Values.OrderByDescending(t => t.BytesDone).Take(8))
                {
                    var est = _estimates.GetValueOrDefault(t.Name);
                    var pct = est > 0 ? Math.Min(99, t.BytesDone * 100 / Math.Max(est, 1)) : 0;
                    grid.AddRow(new Markup($"  {Markup.Escape(t.Name), -48} {ByteSize.Format(t.BytesDone), 10}  ~{pct}%"));
                }
                foreach (var line in _log)
                    grid.AddRow(new Markup("[grey]" + line + "[/]"));
                return grid;
            }
        }
    }
}
