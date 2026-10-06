namespace PgCopy.Core.Orchestration;

public sealed record CloneOptions
{
    /// <summary>Parallel COPY streams. More streams than cores only helps when the network, not the CPU, is the limit.</summary>
    public int Jobs { get; init; } = Math.Clamp(Environment.ProcessorCount, 4, 8);

    /// <summary>Parallel index builds on the target; 0 = half the target's CPUs (at least 2).</summary>
    public int IndexJobs { get; init; }

    /// <summary>Tables larger than this are split into ctid ranges copied in parallel; 0 disables splitting.</summary>
    public long SplitSizeBytes { get; init; } = 128L << 20;

    public bool UseBinary { get; init; } = true;

    /// <summary>Build a table's indexes as soon as its data lands, overlapping with remaining copies.</summary>
    public bool EarlyIndexes { get; init; } = true;

    /// <summary>Mark source-validated foreign keys valid without re-scanning (needs a superuser target).</summary>
    public bool TrustedForeignKeys { get; init; } = true;

    /// <summary>Keep fsync on while loading (slower, survives a power cut mid-load).</summary>
    public bool SafeLoad { get; init; }

    /// <summary>Use an exported snapshot so all tables and the schema are one consistent point in time.</summary>
    public bool UseSnapshot { get; init; } = true;

    public bool SkipMatviewRefresh { get; init; }

    /// <summary>Tables (glob, schema.table) whose rows are skipped; their definitions are still created.</summary>
    public IReadOnlyList<string> ExcludeTableData { get; init; } = [];

    /// <summary>Downgrade pg_restore errors to warnings instead of failing the clone.</summary>
    public bool IgnoreRestoreErrors { get; init; }

    /// <summary>Directory holding pg_dump/pg_restore; auto-detected when empty.</summary>
    public string? PgBinDirectory { get; init; }

    public int EffectiveIndexJobs(int targetCpus) => IndexJobs > 0 ? IndexJobs : Math.Max(2, targetCpus / 2);
}
