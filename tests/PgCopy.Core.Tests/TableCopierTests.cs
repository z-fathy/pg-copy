using PgCopy.Core.Catalog;
using PgCopy.Core.Copy;
using PgCopy.Core.Planning;

namespace PgCopy.Core.Tests;

public class TableCopierTests
{
    private static TableInfo Table(string? condition = null) =>
        new(
            1,
            "Odd Schema",
            "Weird \"x\"",
            100,
            1,
            false,
            condition,
            condition is not null,
            [new ColumnInfo("Select", 23), new ColumnInfo("b", 25)]
        );

    [Fact]
    public void WholeTableExportUsesPlainCopyWithQuotedIdentifiers()
    {
        var task = new CopyTask(Table(), 0, 1, null, null, 100, true);
        Assert.Equal(
            "COPY \"Odd Schema\".\"Weird \"\"x\"\"\" (\"Select\", \"b\") TO STDOUT (FORMAT binary)",
            TableCopier.BuildExport(task, binary: true)
        );
    }

    [Fact]
    public void ChunkExportUsesCtidRangeAndOnly()
    {
        var task = new CopyTask(Table(), 1, 3, 100, 200, 100, true);
        var sql = TableCopier.BuildExport(task, binary: false);
        Assert.Contains("FROM ONLY", sql);
        Assert.Contains("ctid >= '(100,0)'::tid AND ctid < '(200,0)'::tid", sql);
        Assert.EndsWith("TO STDOUT (FORMAT text)", sql);
    }

    [Fact]
    public void LastChunkIsOpenEnded()
    {
        var task = new CopyTask(Table(), 2, 3, 200, null, 100, true);
        var sql = TableCopier.BuildExport(task, binary: true);
        Assert.Contains("ctid >= '(200,0)'::tid", sql);
        Assert.DoesNotContain("ctid <", sql);
    }

    [Fact]
    public void ExtensionConfigConditionIsApplied()
    {
        var task = new CopyTask(Table("id > 1000"), 0, 1, null, null, 100, true);
        Assert.Contains("WHERE (id > 1000)", TableCopier.BuildExport(task, binary: true));
    }

    [Fact]
    public void ImportAddsFreezeOnlyWhenRequested()
    {
        var task = new CopyTask(Table(), 0, 1, null, null, 100, true);
        Assert.EndsWith("FROM STDIN (FORMAT binary, FREEZE)", TableCopier.BuildImport(task, binary: true, freeze: true));
        Assert.EndsWith("FROM STDIN (FORMAT text)", TableCopier.BuildImport(task, binary: false, freeze: false));
    }
}
