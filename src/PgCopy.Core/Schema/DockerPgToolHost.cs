using PgCopy.Core.Connections;

namespace PgCopy.Core.Schema;

/// <summary>Runs the PostgreSQL client tools inside the managed container, guaranteeing a version match.</summary>
public sealed class DockerPgToolHost : IPgToolHost
{
    private readonly string _container;
    private readonly string _workDir = "/tmp/pgcopy-" + Guid.NewGuid().ToString("N")[..10];
    private bool _created;

    public DockerPgToolHost(string container) => _container = container;

    public string Description => $"PostgreSQL tools inside Docker container '{_container}'";

    public ConnectionProfile SourceFor(ConnectionProfile source) =>
        source.Host is "localhost" or "127.0.0.1" or "::1" ? source with { Host = "host.docker.internal" } : source;

    // Inside the container the server listens on its default port.
    public ConnectionProfile TargetFor(ConnectionProfile target) => target with { Host = "localhost", Port = 5432 };

    public string PathOf(string fileName) => $"{_workDir}/{fileName}";

    public async Task WriteFileAsync(string fileName, string content, CancellationToken ct)
    {
        await EnsureWorkDirAsync(ct);
        var local = Path.Combine(Path.GetTempPath(), "pgcopy-" + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(local, content, ct);
        try
        {
            var result = await ProcessRunner.RunAsync("docker", ["cp", local, $"{_container}:{PathOf(fileName)}"], null, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"docker cp failed: {result.StdErr}");
        }
        finally
        {
            File.Delete(local);
        }
    }

    public async Task<ToolResult> RunAsync(
        string tool,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct
    )
    {
        await EnsureWorkDirAsync(ct);
        var dockerArgs = new List<string> { "exec" };
        foreach (var (k, v) in env)
        {
            dockerArgs.Add("-e");
            dockerArgs.Add($"{k}={v}");
        }
        dockerArgs.Add(_container);
        dockerArgs.Add(tool);
        dockerArgs.AddRange(args);
        return await ProcessRunner.RunAsync("docker", dockerArgs, null, ct);
    }

    private async Task EnsureWorkDirAsync(CancellationToken ct)
    {
        if (_created)
            return;
        var result = await ProcessRunner.RunAsync("docker", ["exec", _container, "mkdir", "-p", _workDir], null, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not create scratch dir in container: {result.StdErr}");
        _created = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_created)
            await ProcessRunner.RunAsync("docker", ["exec", _container, "rm", "-rf", _workDir], null, CancellationToken.None);
    }
}
