using System.Text;
using Npgsql;
using PgCopy.Core.Catalog;
using PgCopy.Core.Target;

namespace PgCopy.Core.PostData;

public static class SequenceRestorer
{
    public static async Task RestoreAsync(NpgsqlConnection target, IReadOnlyList<SequenceValue> sequences, CancellationToken ct)
    {
        foreach (var batch in sequences.Chunk(500))
        {
            var sql = new StringBuilder();
            foreach (var s in batch)
            {
                var qualified = TableInfo.Quote(s.Schema) + "." + TableInfo.Quote(s.Name);
                sql.Append($"SELECT pg_catalog.setval({DatabaseAdmin.Literal(qualified)}, {s.LastValue}, true);");
            }
            await DatabaseAdmin.ExecAsync(target, sql.ToString(), ct);
        }
    }
}
