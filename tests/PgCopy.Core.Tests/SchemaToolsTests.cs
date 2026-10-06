using PgCopy.Core.Schema;

namespace PgCopy.Core.Tests;

public class SchemaToolsTests
{
    [Fact]
    public void ParsesErrorBlocksWithCommands()
    {
        const string stderr = """
            pg_restore: while PROCESSING TOC:
            pg_restore: from TOC entry 2; 3079 13 EXTENSION plpgsql
            pg_restore: error: could not execute query: ERROR:  must be owner of extension plpgsql
            Command was: COMMENT ON EXTENSION plpgsql IS 'PL/pgSQL procedural language';
            pg_restore: from TOC entry 203; 1259 20800 TABLE customers
            pg_restore: error: could not execute query: ERROR:  type "foo" does not exist
            LINE 3: x foo
            Command was: CREATE TABLE public.customers (
            pg_restore: warning: errors ignored on restore: 2
            """;

        var errors = SchemaTools.ParseErrors(stderr);

        Assert.Equal(2, errors.Count);
        Assert.True(errors[0].IsBenign);
        Assert.False(errors[1].IsBenign);
        Assert.Contains("does not exist", errors[1].Message);
        Assert.StartsWith("CREATE TABLE", errors[1].Command);
    }

    [Fact]
    public void NoErrorsYieldsEmptyList() =>
        Assert.Empty(SchemaTools.ParseErrors("pg_restore: connecting to database for restore\n"));
}
