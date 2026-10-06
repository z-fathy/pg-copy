using Npgsql;

namespace PgCopy.Core.Catalog;

/// <summary>
/// Reads everything the engine needs to plan a clone. All reads go through the connection that
/// owns the exported snapshot so the catalog view matches the schema dump and the data copy.
/// </summary>
public static class SourceCatalog
{
    private const uint FirstUserOid = 16384;

    public static async Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            select datname, pg_database_size(oid)
            from pg_database
            where not datistemplate and datallowconn and has_database_privilege(datname, 'CONNECT')
            order by pg_database_size(oid) desc
            """;
        var result = new List<DatabaseListing>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new DatabaseListing(r.GetString(0), r.GetInt64(1)));
        return result;
    }

    public static async Task<SourceInfo> ReadInfoAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var versionNum = int.Parse((await ScalarAsync<string>(conn, "show server_version_num", ct))!);
        var providerCol = versionNum >= 150000 ? "d.datlocprovider::text" : "'c'";
        var icuCol =
            versionNum >= 170000 ? "d.datlocale"
            : versionNum >= 150000 ? "d.daticulocale"
            : "null";
        var sql = $"""
            select current_setting('server_version'), pg_encoding_to_char(d.encoding), d.datcollate, d.datctype,
                   {providerCol}, {icuCol}, pg_database_size(d.oid),
                   coalesce((select rolsuper from pg_roles where rolname = current_user), false)
            from pg_database d where d.datname = current_database()
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new SourceInfo(
            versionNum,
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetInt64(6),
            r.GetBoolean(7)
        );
    }

    public static async Task<SourceCatalogSnapshot> ReadAsync(
        NpgsqlConnection conn,
        IReadOnlyCollection<string> excludeDataPatterns,
        CancellationToken ct
    )
    {
        var info = await ReadInfoAsync(conn, ct);
        var tables = await ReadTablesAsync(conn, excludeDataPatterns, ct);
        var binarySafe = await ReadBinarySafeTypesAsync(conn, ct);
        var (early, fks) = await ReadIndexesAndForeignKeysAsync(conn, ct);
        var tableOids = tables.Select(t => t.Oid).ToHashSet();

        return new SourceCatalogSnapshot(
            info,
            tables,
            early.Where(i => tableOids.Contains(i.TableOid)).ToList(),
            fks,
            await StringsAsync(conn, "select extname from pg_extension where extname <> 'plpgsql' order by 1", ct),
            await StringsAsync(
                conn,
                """
                select distinct r.rolname from pg_policy p
                cross join lateral unnest(p.polroles) as o(oid)
                join pg_roles r on r.oid = o.oid where o.oid <> 0
                """,
                ct
            ),
            await ReadDatabaseSettingsAsync(conn, ct),
            await ScalarAsync<long>(conn, "select count(*) from pg_largeobject_metadata", ct),
            await StringsAsync(
                conn,
                """
                select n.nspname || '.' || c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace
                where c.relrowsecurity and c.relkind in ('r','p') and not (select rolbypassrls or rolsuper from pg_roles where rolname = current_user)
                """,
                ct
            ),
            await StringsAsync(
                conn,
                """
                select n.nspname || '.' || c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace
                where c.relkind in ('r','p') and n.nspname not in ('pg_catalog','information_schema') and n.nspname not like 'pg\_toast%'
                  and not has_table_privilege(c.oid, 'SELECT')
                """,
                ct
            ),
            binarySafe,
            await ReadMaterializedViewsAsync(conn, ct)
        );
    }

    /// <summary>
    /// Materialized views ordered so each is refreshed after the matviews it (transitively, through plain
    /// views) reads from. <c>pg_dump -s</c> creates them WITH NO DATA and emits no refresh.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadMaterializedViewsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            with recursive edges as (
                select r.ev_class as rel, d.refobjid as ref
                from pg_depend d join pg_rewrite r on r.oid = d.objid
                where d.classid = 'pg_rewrite'::regclass and d.refclassid = 'pg_class'::regclass and r.ev_class <> d.refobjid
            ), walk as (
                select rel, ref from edges
                union
                select w.rel, e.ref from walk w join edges e on e.rel = w.ref
            )
            select c.oid, format('%I.%I', n.nspname, c.relname),
                   coalesce((select array_agg(w.ref) from walk w join pg_class rc on rc.oid = w.ref and rc.relkind = 'm' where w.rel = c.oid), '{}')
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where c.relkind = 'm' and not exists (
                select 1 from pg_depend d where d.classid = 'pg_class'::regclass and d.objid = c.oid and d.deptype = 'e')
            order by 2
            """;
        var names = new Dictionary<uint, string>();
        var deps = new Dictionary<uint, uint[]>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var oid = (uint)r.GetValue(0);
                names[oid] = r.GetString(1);
                deps[oid] = (uint[])r.GetValue(2);
            }
        }

        var ordered = new List<string>();
        var visited = new HashSet<uint>();
        void Visit(uint oid)
        {
            if (!visited.Add(oid))
                return;
            foreach (var dep in deps[oid].Where(names.ContainsKey))
                Visit(dep);
            ordered.Add(names[oid]);
        }
        foreach (var oid in names.Keys)
            Visit(oid);
        return ordered;
    }

    private static async Task<List<TableInfo>> ReadTablesAsync(
        NpgsqlConnection conn,
        IReadOnlyCollection<string> excludePatterns,
        CancellationToken ct
    )
    {
        // Leaf relations only (relkind 'r' also covers partitions). Extension member tables are
        // skipped unless the extension registered them as configuration tables (pg_dump semantics).
        const string tableSql = """
            select c.oid, n.nspname, c.relname, pg_relation_size(c.oid), c.relpages, c.relispartition,
                   cfg.cond,
                   cfg.oid is not null
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            left join lateral (
                select e.extconfig[i] as oid, nullif(e.extcondition[i], '') as cond
                from pg_extension e, generate_subscripts(e.extconfig, 1) i
                where e.extconfig[i] = c.oid
                limit 1
            ) cfg on true
            where c.relkind = 'r'
              and n.nspname not in ('pg_catalog', 'information_schema')
              and n.nspname not like 'pg\_toast%' and n.nspname not like 'pg\_temp%'
              and (
                  cfg.oid is not null
                  or not exists (
                      select 1 from pg_depend d
                      where d.classid = 'pg_class'::regclass and d.objid = c.oid and d.deptype = 'e'
                  )
              )
            order by pg_relation_size(c.oid) desc, n.nspname, c.relname
            """;
        const string columnSql = """
            select a.attrelid, a.attname, a.atttypid
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            where c.relkind = 'r' and a.attnum > 0 and not a.attisdropped and a.attgenerated = ''
            order by a.attrelid, a.attnum
            """;

        var columns = new Dictionary<uint, List<ColumnInfo>>();
        await using (var cmd = new NpgsqlCommand(columnSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var oid = (uint)r.GetValue(0);
                if (!columns.TryGetValue(oid, out var list))
                    columns[oid] = list = [];
                list.Add(new ColumnInfo(r.GetString(1), (uint)r.GetValue(2)));
            }
        }

        var tables = new List<TableInfo>();
        await using (var cmd = new NpgsqlCommand(tableSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var oid = (uint)r.GetValue(0);
                if (!columns.TryGetValue(oid, out var cols) || cols.Count == 0)
                    continue;
                var schema = r.GetString(1);
                var name = r.GetString(2);
                if (excludePatterns.Any(p => GlobMatch(p, $"{schema}.{name}")))
                    continue;
                tables.Add(
                    new TableInfo(
                        oid,
                        schema,
                        name,
                        r.GetInt64(3),
                        r.GetInt64(4),
                        r.GetBoolean(5),
                        r.IsDBNull(6) ? null : r.GetString(6),
                        r.GetBoolean(7),
                        cols
                    )
                );
            }
        }
        return tables;
    }

    /// <summary>
    /// Type OIDs whose binary COPY representation is portable between servers: built-in types
    /// (stable OIDs), enums (sent as labels) and domains over those. User-defined arrays, composites,
    /// ranges and extension types embed or depend on server-specific OIDs, so they use text COPY.
    /// </summary>
    private static async Task<IReadOnlySet<uint>> ReadBinarySafeTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var safe = new HashSet<uint>();
        var domains = new Dictionary<uint, uint>();
        await using var cmd = new NpgsqlCommand(
            "select oid, typtype::text, typbasetype from pg_type where oid >= 16384 and typtype in ('d','e')",
            conn
        );
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var oid = (uint)r.GetValue(0);
            if (r.GetString(1) == "e")
                safe.Add(oid);
            else
                domains[oid] = (uint)r.GetValue(2);
        }
        foreach (var (oid, _) in domains)
        {
            var current = oid;
            var guard = 0;
            while (domains.TryGetValue(current, out var next) && guard++ < 32)
                current = next;
            if (current < FirstUserOid || safe.Contains(current))
                safe.Add(oid);
        }
        return safe;
    }

    public static bool IsBinarySafe(TableInfo table, IReadOnlySet<uint> safeUserTypes) =>
        table.Columns.All(c => c.TypeOid < FirstUserOid || safeUserTypes.Contains(c.TypeOid));

    private static async Task<(List<IndexDefinition> Early, List<ForeignKeyDefinition> Fks)> ReadIndexesAndForeignKeysAsync(
        NpgsqlConnection conn,
        CancellationToken ct
    )
    {
        // Indexes on plain (non-partitioned, non-partition) tables, excluding ones with customised
        // per-column statistics targets, which the post-data restore handles faithfully.
        const string indexSql = """
            select i.indrelid, i.indexrelid, con.oid, ic.relname,
                   (case when con.oid is not null
                        then format('ALTER TABLE ONLY %I.%I ADD CONSTRAINT %I %s', n.nspname, t.relname, con.conname, pg_get_constraintdef(con.oid))
                        else pg_get_indexdef(i.indexrelid) end)
                   -- pg_dump folds these into the index's own TOC entry, so the early build must apply them too.
                   || (case when i.indisclustered then format('; ALTER TABLE ONLY %I.%I CLUSTER ON %I', n.nspname, t.relname, ic.relname) else '' end)
                   || (case when i.indisreplident then format('; ALTER TABLE ONLY %I.%I REPLICA IDENTITY USING INDEX %I', n.nspname, t.relname, ic.relname) else '' end)
            from pg_index i
            join pg_class ic on ic.oid = i.indexrelid
            join pg_class t on t.oid = i.indrelid
            join pg_namespace n on n.oid = t.relnamespace
            left join pg_constraint con on con.conindid = i.indexrelid and con.contype in ('p','u','x') and con.conrelid = i.indrelid
            where t.relkind = 'r' and not t.relispartition and ic.relkind = 'i' and not ic.relispartition
              and n.nspname not in ('pg_catalog','information_schema') and n.nspname not like 'pg\_toast%'
              and i.indisvalid
              and not exists (
                  select 1 from pg_attribute a
                  where a.attrelid = i.indexrelid and coalesce(a.attstattarget, -1) <> -1
              )
              and not exists (
                  select 1 from pg_depend d
                  where d.classid = 'pg_class'::regclass and d.objid = t.oid and d.deptype = 'e'
              )
            order by pg_relation_size(i.indexrelid) desc
            """;
        var early = new List<IndexDefinition>();
        await using (var cmd = new NpgsqlCommand(indexSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                early.Add(
                    new IndexDefinition(
                        (uint)r.GetValue(0),
                        (uint)r.GetValue(1),
                        r.IsDBNull(2) ? null : (uint)r.GetValue(2),
                        r.GetString(3),
                        r.GetString(4)
                    )
                );
            }
        }

        const string fkSql = """
            select con.oid, format('%I.%I', tn.nspname, t.relname), con.conname, pg_get_constraintdef(con.oid)
            from pg_constraint con
            join pg_class t on t.oid = con.conrelid and t.relkind = 'r' and not t.relispartition
            join pg_namespace tn on tn.oid = t.relnamespace
            join pg_class f on f.oid = con.confrelid and f.relkind = 'r' and not f.relispartition
            where con.contype = 'f' and con.convalidated and con.conparentid = 0
            """;
        var fks = new List<ForeignKeyDefinition>();
        await using (var cmd = new NpgsqlCommand(fkSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                fks.Add(new ForeignKeyDefinition((uint)r.GetValue(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        return (early, fks);
    }

    private static async Task<IReadOnlyList<(string Name, string[] Settings)>> ReadDatabaseSettingsAsync(
        NpgsqlConnection conn,
        CancellationToken ct
    )
    {
        var result = new List<(string, string[])>();
        await using var cmd = new NpgsqlCommand(
            "select setconfig from pg_db_role_setting where setrole = 0 and setdatabase = (select oid from pg_database where datname = current_database())",
            conn
        );
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            foreach (var entry in (string[])r.GetValue(0))
            {
                var idx = entry.IndexOf('=');
                if (idx > 0)
                    result.Add((entry[..idx], [entry[(idx + 1)..]]));
            }
        }
        return result;
    }

    public static async Task<IReadOnlyList<SequenceValue>> ReadSequencesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var result = new List<SequenceValue>();
        await using var cmd = new NpgsqlCommand(
            "select schemaname, sequencename, last_value from pg_sequences where last_value is not null",
            conn
        );
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new SequenceValue(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return result;
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<IReadOnlyList<string>> StringsAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        var result = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(r.GetString(0));
        return result;
    }

    /// <summary>Case-insensitive glob where '*' matches any run of characters and '?' one character.</summary>
    public static bool GlobMatch(string pattern, string value)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
    }
}
