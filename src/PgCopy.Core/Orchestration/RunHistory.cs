using System.Text.Json;

namespace PgCopy.Core.Orchestration;

public sealed record RunRecord(
    DateTimeOffset At,
    string Source,
    string SourceDatabase,
    string TargetDatabase,
    bool Success,
    string? Error,
    double TotalSeconds,
    long Bytes,
    double AverageMBps,
    double PeakMBps,
    int Tables,
    int Indexes,
    IReadOnlyDictionary<string, double> PhaseSeconds
)
{
    public static RunRecord FromReport(string source, CloneReport r) =>
        new(
            DateTimeOffset.Now,
            source,
            r.SourceDatabase,
            r.TargetDatabase,
            true,
            null,
            r.Total.TotalSeconds,
            r.BytesTransferred,
            r.AverageMBps,
            r.PeakMBps,
            r.Tables,
            r.Indexes,
            r.PhaseTimes.ToDictionary(p => p.Key.ToString(), p => Math.Round(p.Value.TotalSeconds, 2))
        );

    public static RunRecord FromFailure(string source, string sourceDb, string targetDb, string error, TimeSpan elapsed) =>
        new(
            DateTimeOffset.Now,
            source,
            sourceDb,
            targetDb,
            false,
            error,
            elapsed.TotalSeconds,
            0,
            0,
            0,
            0,
            0,
            new Dictionary<string, double>()
        );
}

/// <summary>Append-only JSON-lines history of clone runs, shared by the CLI and the desktop app.</summary>
public sealed class RunHistory
{
    private readonly string _path;

    public RunHistory(string? path = null) => _path = path ?? Path.Combine(AppPaths.DataDirectory, "history.jsonl");

    public void Append(RunRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        catch (IOException) { }
    }

    public IReadOnlyList<RunRecord> Load(int max = 200)
    {
        if (!File.Exists(_path))
            return [];
        var result = new List<RunRecord>();
        foreach (var line in File.ReadLines(_path).TakeLast(max))
        {
            try
            {
                if (JsonSerializer.Deserialize<RunRecord>(line) is { } record)
                    result.Add(record);
            }
            catch (JsonException) { }
        }
        result.Reverse();
        return result;
    }
}
