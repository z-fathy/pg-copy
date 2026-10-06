using System.Diagnostics;
using System.Text;

namespace PgCopy.Core.Schema;

public sealed record ToolResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static async Task<ToolResult> RunAsync(
        string executable,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (env is not null)
        {
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException($"Could not start '{executable}': {ex.Message}", executable, ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
        return new ToolResult(process.ExitCode, await stdout, await stderr);
    }
}
