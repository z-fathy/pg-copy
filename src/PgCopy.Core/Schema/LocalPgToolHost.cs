using PgCopy.Core.Connections;

namespace PgCopy.Core.Schema;

public sealed class LocalPgToolHost : IPgToolHost
{
    private readonly string _binDir;
    private readonly string _workDir;

    public LocalPgToolHost(string binDir)
    {
        _binDir = binDir;
        _workDir = Path.Combine(Path.GetTempPath(), "pgcopy-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_workDir);
    }

    public string Description => $"local PostgreSQL tools ({_binDir})";

    public ConnectionProfile SourceFor(ConnectionProfile source) => source;

    public ConnectionProfile TargetFor(ConnectionProfile target) => target;

    public string PathOf(string fileName) => Path.Combine(_workDir, fileName);

    public Task WriteFileAsync(string fileName, string content, CancellationToken ct) =>
        File.WriteAllTextAsync(PathOf(fileName), content, ct);

    public Task<ToolResult> RunAsync(
        string tool,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct
    )
    {
        var exe = Path.Combine(_binDir, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
        return ProcessRunner.RunAsync(exe, args, env, ct);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
