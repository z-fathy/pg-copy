using PgCopy.Core.Catalog;

namespace PgCopy.Core.Planning;

/// <summary>One unit of copy work: a whole table, or one ctid page range of a large table.</summary>
public sealed record CopyTask(
    TableInfo Table,
    int ChunkIndex,
    int ChunkCount,
    long? StartPage,
    long? EndPage,
    long EstimatedBytes,
    bool Binary
)
{
    public bool IsWholeTable => ChunkCount == 1;
    public string Label => IsWholeTable ? Table.DisplayName : $"{Table.DisplayName} [{ChunkIndex + 1}/{ChunkCount}]";
}
