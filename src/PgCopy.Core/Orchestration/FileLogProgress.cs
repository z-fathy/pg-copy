namespace PgCopy.Core.Orchestration;

/// <summary>Writes phase changes, log lines and periodic throughput to a log file under %LOCALAPPDATA%\pg-copy\logs.</summary>
public sealed class FileLogProgress : IProgress<CloneEvent>, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private long _lastStatus;

    public string Path { get; }

    public FileLogProgress(string database)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        var safe = string.Concat(database.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        Path = System.IO.Path.Combine(AppPaths.LogDirectory, $"clone-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(Path) { AutoFlush = true };
    }

    public void Report(CloneEvent value)
    {
        lock (_gate)
        {
            switch (value)
            {
                case PhaseEvent p:
                    Write($"== {p.Phase}");
                    break;
                case LogEvent l:
                    Write($"{(l.IsWarning ? "WARN " : "")}{l.Message}");
                    break;
                case ProgressEvent p when Environment.TickCount64 - _lastStatus > 5000:
                    _lastStatus = Environment.TickCount64;
                    Write(
                        $"   {p.BytesTransferred / 1048576} MB, {p.CurrentMBps:0.0} MB/s, tables {p.TablesDone}/{p.TablesTotal}, indexes {p.IndexesDone}"
                    );
                    break;
            }
        }
    }

    public void WriteLine(string text)
    {
        lock (_gate)
            Write(text);
    }

    private void Write(string text) => _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");

    public void Dispose() => _writer.Dispose();
}
