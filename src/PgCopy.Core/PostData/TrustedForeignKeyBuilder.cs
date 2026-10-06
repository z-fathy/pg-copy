using Npgsql;
using PgCopy.Core.Catalog;
using PgCopy.Core.Target;

namespace PgCopy.Core.PostData;

/// <summary>
/// Adds foreign keys without the validation scan. The rows came from one consistent snapshot of a database
/// where these constraints were already validated, so flipping convalidated is sound and turns a full scan
/// per constraint into a catalog update. A key that cannot be added this way is left to the post-data restore.
/// </summary>
public static class TrustedForeignKeyBuilder
{
    /// <returns>The constraint OIDs (on the source) that were created.</returns>
    public static async Task<IReadOnlyList<uint>> AddAsync(
        NpgsqlConnection target,
        IReadOnlyList<ForeignKeyDefinition> foreignKeys,
        Action<string> warn,
        CancellationToken ct
    )
    {
        var created = new List<uint>();
        if (foreignKeys.Count == 0)
            return created;
        await DatabaseAdmin.ExecAsync(target, "SET search_path = ''", ct);
        await DatabaseAdmin.ExecAsync(target, "BEGIN", ct);
        try
        {
            foreach (var fk in foreignKeys)
            {
                await DatabaseAdmin.ExecAsync(target, "SAVEPOINT fk", ct);
                try
                {
                    await DatabaseAdmin.ExecAsync(
                        target,
                        $"ALTER TABLE ONLY {fk.TableQualified} ADD CONSTRAINT {DatabaseAdmin.Ident(fk.ConstraintName)} {fk.Definition} NOT VALID",
                        ct
                    );
                    await DatabaseAdmin.ExecAsync(
                        target,
                        $"UPDATE pg_catalog.pg_constraint SET convalidated = true WHERE conrelid = {DatabaseAdmin.Literal(fk.TableQualified)}::regclass AND conname = {DatabaseAdmin.Literal(fk.ConstraintName)}",
                        ct
                    );
                    await DatabaseAdmin.ExecAsync(target, "RELEASE SAVEPOINT fk", ct);
                    created.Add(fk.ConstraintOid);
                }
                catch (PostgresException ex)
                {
                    await DatabaseAdmin.ExecAsync(target, "ROLLBACK TO SAVEPOINT fk", ct);
                    warn(
                        $"Foreign key {fk.ConstraintName} on {fk.TableQualified} deferred to post-data restore ({ex.MessageText})."
                    );
                }
            }
            await DatabaseAdmin.ExecAsync(target, "COMMIT", ct);
        }
        catch
        {
            await DatabaseAdmin.ExecAsync(target, "ROLLBACK", CancellationToken.None);
            throw;
        }
        return created;
    }
}
