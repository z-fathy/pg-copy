using System.Text.RegularExpressions;
using PgCopy.Core.Connections;

namespace PgCopy.Core.Schema;

public sealed record RestoreError(string Message, string? Command)
{
    // pg_restore tries to COMMENT ON EXTENSION plpgsql etc.; that needs ownership and is cosmetic.
    public bool IsBenign => Command?.StartsWith("COMMENT ON EXTENSION", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record RestoreResult(int ExitCode, IReadOnlyList<RestoreError> Errors, string StdErr);

/// <summary>Thin wrapper over pg_dump/pg_restore used for the parts logical COPY cannot do: DDL and large objects.</summary>
public sealed partial class SchemaTools
{
    private readonly IPgToolHost _host;

    public SchemaTools(IPgToolHost host) => _host = host;

    public async Task DumpAsync(
        ConnectionProfile source,
        string? snapshotId,
        string fileName,
        bool largeObjectsOnly,
        CancellationToken ct
    )
    {
        var args = new List<string> { "-Fc", "--no-owner", "--no-acl", "--no-tablespaces", "--no-security-labels" };
        args.AddRange(["--no-subscriptions", "--no-publications", "--no-sync", "-f", _host.PathOf(fileName)]);
        if (largeObjectsOnly)
            args.AddRange(["--large-objects", "--exclude-table-data=*.*", "-Z", "0"]);
        else
            args.Add("--schema-only");
        if (snapshotId is not null)
            args.Add($"--snapshot={snapshotId}");
        args.AddRange(ConnectionArgs(_host.SourceFor(source), source.Database));

        var result = await _host.RunAsync("pg_dump", args, EnvFor(_host.SourceFor(source)), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    public async Task<TocList> ListAsync(string dumpFile, CancellationToken ct)
    {
        var result = await _host.RunAsync("pg_restore", ["-l", _host.PathOf(dumpFile)], new Dictionary<string, string>(), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"pg_restore -l failed: {result.StdErr.Trim()}");
        return TocList.Parse(result.StdOut);
    }

    public Task WriteListAsync(string fileName, string content, CancellationToken ct) =>
        _host.WriteFileAsync(fileName, content, ct);

    public async Task<RestoreResult> RestoreAsync(
        string dumpFile,
        ConnectionProfile target,
        string database,
        string? section,
        string? listFile,
        int jobs,
        CancellationToken ct
    )
    {
        var args = new List<string> { "--no-owner", "--no-acl" };
        if (section is not null)
            args.Add($"--section={section}");
        if (listFile is not null)
            args.AddRange(["-L", _host.PathOf(listFile)]);
        if (jobs > 1)
            args.AddRange(["-j", jobs.ToString()]);
        args.AddRange(ConnectionArgs(_host.TargetFor(target), database));
        args.Add(_host.PathOf(dumpFile));

        var result = await _host.RunAsync("pg_restore", args, EnvFor(_host.TargetFor(target)), ct);
        return new RestoreResult(result.ExitCode, ParseErrors(result.StdErr), result.StdErr);
    }

    private static List<string> ConnectionArgs(ConnectionProfile profile, string database) =>
        ["-h", profile.Host, "-p", profile.Port.ToString(), "-U", profile.Username, "-d", database, "-w"];

    private static Dictionary<string, string> EnvFor(ConnectionProfile profile)
    {
        var env = new Dictionary<string, string>
        {
            ["PGSSLMODE"] = profile.SslMode switch
            {
                "VerifyCA" => "verify-ca",
                "VerifyFull" => "verify-full",
                var m => m.ToLowerInvariant(),
            },
            ["PGCONNECT_TIMEOUT"] = "30",
        };
        if (profile.Password is not null)
            env["PGPASSWORD"] = profile.Password;
        return env;
    }

    public static List<RestoreError> ParseErrors(string stderr)
    {
        var errors = new List<RestoreError>();
        string? message = null;
        string? command = null;
        void Flush()
        {
            if (message is not null)
                errors.Add(new RestoreError(message, command));
            message = command = null;
        }

        foreach (var line in stderr.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("pg_restore: error:", StringComparison.Ordinal))
            {
                Flush();
                message = line["pg_restore: error:".Length..].Trim();
            }
            else if (line.StartsWith("pg_restore:", StringComparison.Ordinal))
                Flush();
            else if (message is not null && line.StartsWith("Command was:", StringComparison.Ordinal))
                command = line["Command was:".Length..].Trim();
            else if (message is not null && !string.IsNullOrWhiteSpace(line) && command is null)
                message += " " + line.Trim();
        }
        Flush();
        return errors;
    }
}
