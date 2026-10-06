using PgCopy.Core.Catalog;

namespace PgCopy.Core.Tests;

public class CatalogHelperTests
{
    [Theory]
    [InlineData("audit.*", "audit.log", true)]
    [InlineData("audit.*", "public.audit", false)]
    [InlineData("*.events_*", "sales.events_2024", true)]
    [InlineData("PUBLIC.Users", "public.users", true)]
    [InlineData("a.b?", "a.bc", true)]
    [InlineData("a.b?", "a.bcd", false)]
    public void GlobMatchesSchemaQualifiedNames(string pattern, string name, bool expected) =>
        Assert.Equal(expected, SourceCatalog.GlobMatch(pattern, name));

    [Fact]
    public void QuoteEscapesEmbeddedQuotes() => Assert.Equal("\"a\"\"b\"", TableInfo.Quote("a\"b"));
}
