using System.Security.Cryptography;
using Npgsql;
using PgCopy.Core.Connections;
using PgCopy.Core.Schema;

namespace PgCopy.Core.Target;

/// <summary>
/// A PostgreSQL container owned by pg-copy: one per major version, data in a named volume (ext4 inside
/// the Docker VM, never an NTFS bind mount), bound to loopback only, started with wal_level=minimal so
/// COPY FREEZE into freshly truncated tables skips WAL entirely.
/// </summary>
public sealed class DockerTargetServer : ITargetServer
{
    private static readonly string[] LoadModeSettings = ["fsync", "full_page_writes", "synchronous_commit", "autovacuum"];

    private readonly ISecretStore _secrets;
    private readonly string? _imageOverride;
    private readonly bool _safeLoad;
    private ConnectionProfile? _admin;

    public DockerTargetServer(ISecretStore secrets, string? imageOverride = null, bool safeLoad = false)
    {
        _secrets = secrets;
        _imageOverride = imageOverride;
        _safeLoad = safeLoad;
    }

    public string Name => $"managed Docker container {DockerContainer}";

    public ConnectionProfile Admin => _admin ?? throw new InvalidOperationException("Target not prepared yet.");

    public string? DockerContainer { get; private set; }

    public long MemoryBytes { get; private set; } = 4L << 30;

    public int CpuCount { get; private set; } = Environment.ProcessorCount;

    public static string ContainerNameFor(int major) => $"pgcopy-pg{major}";

    public async Task PrepareAsync(int sourceMajor, Action<string> log, CancellationToken ct)
    {
        var name = ContainerNameFor(sourceMajor);
        DockerContainer = name;

        var info = await Docker(
            ["info", "--format", "{{.MemTotal}} {{.NCPU}}"],
            ct,
            "Docker is not reachable. Start Docker Desktop and try again."
        );
        var parts = info.Trim().Split(' ');
        if (parts.Length == 2 && long.TryParse(parts[0], out var mem) && int.TryParse(parts[1], out var cpu))
            (MemoryBytes, CpuCount) = (mem, cpu);

        var state = await TryDocker(["inspect", "-f", "{{.State.Running}}", name], ct);
        var passwordKey = $"docker:{name}:password";
        var password = _secrets.Get(passwordKey);

        if (state is null)
        {
            password = RandomNumberGenerator.GetHexString(24, lowercase: true);
            _secrets.Set(passwordKey, password);
            log($"Creating container {name} (PostgreSQL {sourceMajor})...");
            await Docker(BuildRunArgs(name, sourceMajor, password), ct);
        }
        else if (state.Trim() != "true")
        {
            log($"Starting container {name}...");
            await Docker(["start", name], ct);
        }

        if (password is null)
            throw new InvalidOperationException(
                $"Container {name} exists but its password is not stored. Remove it with 'docker rm -f {name}' (and its volume) and run again."
            );

        var port = ParsePort(await Docker(["port", name, "5432/tcp"], ct));
        _admin = new ConnectionProfile
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "postgres",
            Password = password,
            Database = "postgres",
            SslMode = "Disable",
        };
        await WaitReadyAsync(ct);
        await ExitLoadModeAsync(ct); // A previous run may have crashed while in load mode.
    }

    private List<string> BuildRunArgs(string name, int major, string password) =>
        BuildRunArgs(name, major, password, _imageOverride, MemoryBytes, CpuCount);

    internal static List<string> BuildRunArgs(
        string name,
        int major,
        string password,
        string? imageOverride,
        long memoryBytes,
        int cpuCount
    )
    {
        var sharedBuffersMb = Math.Clamp(memoryBytes / 4 >> 20, 128, 4096);
        var maintenanceMb = Math.Clamp(memoryBytes / 8 >> 20, 64, 2048);
        var workers = Math.Max(8, cpuCount * 2);
        // PostgreSQL 18 images moved PGDATA under a versioned directory, so the volume mounts one level up.
        var dataPath = major >= 18 ? "/var/lib/postgresql" : "/var/lib/postgresql/data";
        var image = imageOverride ?? $"postgres:{major}";
        return
        [
            "run",
            "-d",
            "--name",
            name,
            "-e",
            $"POSTGRES_PASSWORD={password}",
            "-p",
            "127.0.0.1::5432",
            "-v",
            $"{name}-data:{dataPath}",
            "--shm-size=1g",
            "--add-host",
            "host.docker.internal:host-gateway",
            image,
            "-c",
            "wal_level=minimal",
            "-c",
            "max_wal_senders=0",
            "-c",
            "max_wal_size=16GB",
            "-c",
            "checkpoint_timeout=30min",
            "-c",
            $"shared_buffers={sharedBuffersMb}MB",
            "-c",
            $"maintenance_work_mem={maintenanceMb}MB",
            "-c",
            $"max_worker_processes={workers}",
            "-c",
            $"max_parallel_workers={workers}",
            "-c",
            "max_parallel_maintenance_workers=4",
            "-c",
            "max_connections=200",
        ];
    }

    internal static int ParsePort(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return int.Parse(line[(line.LastIndexOf(':') + 1)..]);
    }

    private async Task WaitReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(Admin.ToConnectionString(ConnectionRole.Plain));
                await conn.OpenAsync(ct);
                await DatabaseAdmin.ExecAsync(conn, "select 1", ct);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or IOException)
            {
                last = ex;
                await Task.Delay(500, ct);
            }
        }
        throw new InvalidOperationException($"PostgreSQL in {DockerContainer} did not become ready: {last?.Message}");
    }

    public async Task EnterLoadModeAsync(CancellationToken ct)
    {
        if (_safeLoad)
            return;
        await AlterSystemAsync(
            [
                "ALTER SYSTEM SET fsync = off",
                "ALTER SYSTEM SET full_page_writes = off",
                "ALTER SYSTEM SET synchronous_commit = off",
                "ALTER SYSTEM SET autovacuum = off",
            ],
            ct
        );
    }

    public Task ExitLoadModeAsync(CancellationToken ct) =>
        AlterSystemAsync(LoadModeSettings.Select(s => $"ALTER SYSTEM RESET {s}").ToList(), ct, checkpoint: true);

    private async Task AlterSystemAsync(IReadOnlyList<string> statements, CancellationToken ct, bool checkpoint = false)
    {
        await using var conn = new NpgsqlConnection(Admin.ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync(ct);
        foreach (var sql in statements)
            await DatabaseAdmin.ExecAsync(conn, sql, ct);
        await DatabaseAdmin.ExecAsync(conn, "select pg_reload_conf()", ct);
        if (checkpoint)
            await DatabaseAdmin.ExecAsync(conn, "CHECKPOINT", ct);
    }

    public async Task StopAsync(int major, CancellationToken ct) => await Docker(["stop", ContainerNameFor(major)], ct);

    public static async Task<string> StatusAsync(int major, CancellationToken ct) =>
        (await TryDocker(["inspect", "-f", "{{.State.Status}}", ContainerNameFor(major)], ct))?.Trim() ?? "not created";

    private static async Task<string> Docker(IReadOnlyList<string> args, CancellationToken ct, string? failure = null)
    {
        ToolResult result;
        try
        {
            result = await ProcessRunner.RunAsync("docker", args, null, ct);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(
                "The 'docker' command was not found. Install Docker Desktop and make sure it is on PATH."
            );
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                failure ?? $"docker {string.Join(' ', args.Take(2))} failed: {result.StdErr.Trim()}"
            );
        return result.StdOut;
    }

    private static async Task<string?> TryDocker(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("docker", args, null, ct);
            return result.ExitCode == 0 ? result.StdOut : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
