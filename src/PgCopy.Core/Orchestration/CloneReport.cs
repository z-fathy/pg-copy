namespace PgCopy.Core.Orchestration;

public sealed record CloneReport(
    string SourceDatabase,
    string TargetDatabase,
    TimeSpan Total,
    IReadOnlyDictionary<ClonePhase, TimeSpan> PhaseTimes,
    long BytesTransferred,
    int Tables,
    int Indexes,
    double AverageMBps,
    double PeakMBps,
    IReadOnlyList<string> Warnings
);
