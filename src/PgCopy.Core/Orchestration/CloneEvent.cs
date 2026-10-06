namespace PgCopy.Core.Orchestration;

public enum ClonePhase
{
    Preflight,
    CreateDatabase,
    Schema,
    DataCopy,
    FinishIndexes,
    ForeignKeys,
    SequencesAndLargeObjects,
    PostData,
    Analyze,
    Swap,
}

public enum TableState
{
    Pending,
    Copying,
    Done,
}

public sealed record TablePlan(string Name, long EstimatedBytes, int Chunks);

public sealed record TableProgress(string Name, long BytesDone, TableState State);

public abstract record CloneEvent;

public sealed record PhaseEvent(ClonePhase Phase) : CloneEvent;

public sealed record LogEvent(bool IsWarning, string Message) : CloneEvent;

public sealed record PlanEvent(IReadOnlyList<TablePlan> Tables, int IndexCount) : CloneEvent;

public sealed record ProgressEvent(
    long BytesTransferred,
    double CurrentMBps,
    int TablesDone,
    int TablesTotal,
    int IndexesDone,
    IReadOnlyList<TableProgress> Changed
) : CloneEvent;
