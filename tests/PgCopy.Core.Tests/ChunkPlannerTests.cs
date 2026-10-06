using PgCopy.Core.Catalog;
using PgCopy.Core.Planning;

namespace PgCopy.Core.Tests;

public class ChunkPlannerTests
{
    private static TableInfo Table(string name, long bytes, bool config = false, uint typeOid = 25) =>
        new(
            Oid: (uint)name.GetHashCode(),
            Schema: "public",
            Name: name,
            Bytes: bytes,
            Pages: bytes / 8192,
            IsPartition: false,
            ExtensionCondition: null,
            IsExtensionConfigTable: config,
            Columns: [new ColumnInfo("id", typeOid)]
        );

    private static readonly IReadOnlySet<uint> NoUserTypes = new HashSet<uint>();

    [Fact]
    public void SmallTablesAreNotSplit()
    {
        var tasks = ChunkPlanner.Plan([Table("a", 10 << 20)], 128L << 20, true, NoUserTypes);
        var task = Assert.Single(tasks);
        Assert.True(task.IsWholeTable);
        Assert.Null(task.StartPage);
        Assert.Null(task.EndPage);
    }

    [Fact]
    public void LargeTablesAreSplitIntoContiguousRangesWithOpenEnd()
    {
        var tasks = ChunkPlanner.Plan([Table("big", 1000L << 20)], 128L << 20, true, NoUserTypes);
        var ordered = tasks.OrderBy(t => t.ChunkIndex).ToList();

        Assert.Equal(8, ordered.Count);
        Assert.All(ordered, t => Assert.Equal(8, t.ChunkCount));
        Assert.Equal(0, ordered[0].StartPage);
        for (var i = 0; i < ordered.Count - 1; i++)
            Assert.Equal(ordered[i].EndPage, ordered[i + 1].StartPage);
        Assert.Null(ordered[^1].EndPage); // covers pages appended after the size was measured
    }

    [Fact]
    public void ExtensionConfigTablesAreNeverSplit()
    {
        var tasks = ChunkPlanner.Plan([Table("cfg", 1000L << 20, config: true)], 128L << 20, true, NoUserTypes);
        Assert.Single(tasks);
    }

    [Fact]
    public void SplittingCanBeDisabled()
    {
        Assert.Single(ChunkPlanner.Plan([Table("big", 1000L << 20)], 0, true, NoUserTypes));
    }

    [Fact]
    public void WorkIsOrderedLargestFirst()
    {
        var tasks = ChunkPlanner.Plan(
            [Table("small", 1 << 20), Table("medium", 50 << 20), Table("tiny", 1 << 10)],
            0,
            true,
            NoUserTypes
        );
        Assert.Equal(["medium", "small", "tiny"], tasks.Select(t => t.Table.Name));
    }

    [Fact]
    public void UserDefinedTypesFallBackToText_UnlessEnumOrDomainOverBuiltin()
    {
        const uint userComposite = 20000;
        const uint userEnum = 20001;
        var safe = new HashSet<uint> { userEnum };

        var composite = ChunkPlanner.Plan([Table("c", 1, typeOid: userComposite)], 0, true, safe).Single();
        var enumTable = ChunkPlanner.Plan([Table("e", 1, typeOid: userEnum)], 0, true, safe).Single();
        var builtin = ChunkPlanner.Plan([Table("b", 1)], 0, true, safe).Single();

        Assert.False(composite.Binary);
        Assert.True(enumTable.Binary);
        Assert.True(builtin.Binary);
        Assert.False(ChunkPlanner.Plan([Table("b", 1)], 0, allowBinary: false, safe).Single().Binary);
    }
}
