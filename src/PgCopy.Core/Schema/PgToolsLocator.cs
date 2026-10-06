using System.Text.RegularExpressions;

namespace PgCopy.Core.Schema;

/// <summary>Finds locally installed pg_dump/pg_restore whose major version is at least the source server's.</summary>
public static partial class PgToolsLocator
{
    public static async Task<string?> FindBinDirAsync(int sourceMajor, string? configuredDir, CancellationToken ct)
    {
        string? best = null;
        var bestMajor = int.MaxValue;
        foreach (var dir in CandidateDirs(configuredDir))
        {
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
            if (
                !File.Exists(exe)
                || !File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore"))
            )
                continue;
            var major = await ReadMajorAsync(exe, ct);
            if (major >= sourceMajor && major < bestMajor)
                (best, bestMajor) = (dir, major);
        }
        return best;
    }

    private static IEnumerable<string> CandidateDirs(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;
        foreach (
            var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
            yield return dir;

        foreach (
            var root in new[]
            {
                @"C:\Program Files\PostgreSQL",
                @"C:\Program Files (x86)\PostgreSQL",
                "/usr/lib/postgresql",
                "/usr/local/pgsql",
            }
        )
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var version in Directory.EnumerateDirectories(root))
                yield return Path.Combine(version, "bin");
        }
    }

    private static async Task<int> ReadMajorAsync(string exe, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(exe, ["--version"], null, ct);
            var m = VersionPattern().Match(result.StdOut);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    [GeneratedRegex(@"(\d+)(?:\.\d+)*")]
    private static partial Regex VersionPattern();
}
