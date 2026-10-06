using PgCopy.Core.Catalog;

namespace PgCopy.Core.Planning;

public static class ChunkPlanner
{
    private const long PageSize = 8192;

    /// <summary>
    /// Builds the ordered work list. Tables bigger than <paramref name="splitSizeBytes"/> become several
    /// ctid ranges (served by TID Range Scans). The list is largest-first so the long poles start early
    /// (longest-processing-time scheduling).
    /// </summary>
    public static List<CopyTask> Plan(
        IEnumerable<TableInfo> tables,
        long splitSizeBytes,
        bool allowBinary,
        IReadOnlySet<uint> binarySafeTypes
    )
    {
        var tasks = new List<CopyTask>();
        foreach (var table in tables)
        {
            var binary = allowBinary && SourceCatalog.IsBinarySafe(table, binarySafeTypes);
            var pages = Math.Max(table.Pages, (table.Bytes + PageSize - 1) / PageSize);
            var canSplit = splitSizeBytes > 0 && table.Bytes > splitSizeBytes && pages > 1 && !table.IsExtensionConfigTable;
            if (!canSplit)
            {
                tasks.Add(new CopyTask(table, 0, 1, null, null, table.Bytes, binary));
                continue;
            }

            var count = (int)Math.Min(1024, (table.Bytes + splitSizeBytes - 1) / splitSizeBytes);
            var pagesPerChunk = (pages + count - 1) / count;
            for (var i = 0; i < count; i++)
            {
                var start = i * pagesPerChunk;
                // The last chunk is open-ended so pages appended after relpages was read are still covered.
                long? end = i == count - 1 ? null : start + pagesPerChunk;
                tasks.Add(new CopyTask(table, i, count, start, end, table.Bytes / count, binary));
            }
        }
        return tasks.OrderByDescending(t => t.EstimatedBytes).ThenBy(t => t.Table.DisplayName).ToList();
    }
}
