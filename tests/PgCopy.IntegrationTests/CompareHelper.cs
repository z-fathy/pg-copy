using System.Diagnostics;
using PgCopy.Core.Connections;

namespace PgCopy.IntegrationTests;

/// <summary>Runs Scripts/compare.sh, which diffs schema, rows, sequences, LOs, constraints and settings.</summary>
public static class CompareHelper
{
    public static async Task<(int ExitCode, string Output)> RunAsync(
        ConnectionProfile source,
        ConnectionProfile target,
        string sourceDb,
        string targetDb
    )
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Scripts", "compare.sh"));
        psi.ArgumentList.Add(source.Port.ToString());
        psi.ArgumentList.Add(target.Port.ToString());
        psi.ArgumentList.Add(sourceDb);
        psi.ArgumentList.Add(targetDb);
        psi.Environment["PGHOST_OVERRIDE"] = source.Host;
        if (source.Password is not null)
            psi.Environment["PGPASSWORD"] = source.Password;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
