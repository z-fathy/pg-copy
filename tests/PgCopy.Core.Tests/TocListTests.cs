using PgCopy.Core.Schema;

namespace PgCopy.Core.Tests;

public class TocListTests
{
    private const string Sample = """
        ;
        ; Archive created at 2026-10-06 07:00:00 UTC
        ;     dbname: zoo
        ;
        ; Selected TOC Entries:
        ;
        203; 1259 20800 TABLE sales customers postgres
        4153; 2606 20850 CONSTRAINT sales customers customers_pkey postgres
        4170; 1259 20851 INDEX sales customers_name_trgm postgres
        4180; 0 0 INDEX ATTACH sales events_kind_idx_attach postgres
        4190; 2606 20870 FK CONSTRAINT sales orders orders_customer_id_fkey postgres
        4200; 0 0 USER MAPPING - USER MAPPING postgres SERVER srv
        """;

    [Fact]
    public void ParsesEntriesAndSkipsComments()
    {
        var toc = TocList.Parse(Sample);
        Assert.Equal(6, toc.Entries.Count());
        var pk = toc.Entries.Single(e => e.CatalogOid == 20850);
        Assert.Equal(TocList.PgConstraintTable, pk.CatalogTable);
        Assert.True(TocList.IsType(pk, "CONSTRAINT"));
        Assert.False(TocList.IsType(pk, "FK CONSTRAINT"));
    }

    [Fact]
    public void DisablesEntriesByCatalogIdNotByName()
    {
        var toc = TocList.Parse(Sample);
        var rendered = toc.Render(e =>
            (e.CatalogTable, e.CatalogOid) is (TocList.PgConstraintTable, 20850) or (TocList.PgClassTable, 20851)
        );

        Assert.Contains(";4153; 2606 20850 CONSTRAINT", rendered);
        Assert.Contains(";4170; 1259 20851 INDEX", rendered);
        Assert.Contains("\n203; 1259 20800 TABLE", rendered); // table untouched
        Assert.Contains("\n4180; 0 0 INDEX ATTACH", rendered); // zero ids are never matched
        Assert.Contains("\n4190; 2606 20870 FK CONSTRAINT", rendered);
    }

    [Fact]
    public void TypeMatchDistinguishesIndexFromIndexAttach()
    {
        var toc = TocList.Parse(Sample);
        Assert.Single(toc.Entries, e => TocList.IsType(e, "INDEX"));
        Assert.Single(toc.Entries, e => TocList.IsType(e, "INDEX ATTACH"));
        Assert.Single(toc.Entries, e => TocList.IsType(e, "USER MAPPING"));
        Assert.Single(toc.Entries, e => TocList.IsType(e, "TABLE"));
        Assert.Single(toc.Entries, e => TocList.IsType(e, "FK CONSTRAINT"));
    }
}
