using Npgsql;

namespace PgCopy.Core.Target;

/// <summary>
/// Database-level <c>ALTER DATABASE … SET</c> values. They live in pg_db_role_setting, so neither a template
/// copy nor a schema-only dump carries them; they have to be re-applied explicitly.
/// </summary>
public static class DatabaseSettings
{
    // GUC_LIST_QUOTE parameters hold comma separated identifiers that must be re-quoted one by one.
    private static readonly string[] ListParameters =
    [
        "search_path",
        "temp_tablespaces",
        "session_preload_libraries",
        "local_preload_libraries",
    ];

    public static async Task<IReadOnlyList<(string Name, string Value)>> ReadAsync(
        NpgsqlConnection admin,
        string database,
        CancellationToken ct
    )
    {
        var result = new List<(string, string)>();
        await using var cmd = new NpgsqlCommand(
            "select setconfig from pg_db_role_setting where setrole = 0 and setdatabase = (select oid from pg_database where datname = $1)",
            admin
        );
        cmd.Parameters.AddWithValue(database);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            foreach (var entry in (string[])r.GetValue(0))
            {
                var idx = entry.IndexOf('=');
                if (idx > 0)
                    result.Add((entry[..idx], entry[(idx + 1)..]));
            }
        }
        return result;
    }

    public static async Task ApplyAsync(
        NpgsqlConnection admin,
        string database,
        IEnumerable<(string Name, string Value)> settings,
        CancellationToken ct
    )
    {
        foreach (var (name, raw) in settings)
        {
            var rendered = ListParameters.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? string.Join(", ", raw.Split(',').Select(v => DatabaseAdmin.Literal(v.Trim().Trim('"'))))
                : DatabaseAdmin.Literal(raw);
            try
            {
                await DatabaseAdmin.ExecAsync(
                    admin,
                    $"ALTER DATABASE {DatabaseAdmin.Ident(database)} SET {name} = {rendered}",
                    ct
                );
            }
            catch (PostgresException)
            {
                // Setting unknown on this server (e.g. an extension GUC); not worth failing over.
            }
        }
    }

    public static async Task CopyAsync(NpgsqlConnection admin, string from, string to, CancellationToken ct) =>
        await ApplyAsync(admin, to, await ReadAsync(admin, from, ct), ct);
}
