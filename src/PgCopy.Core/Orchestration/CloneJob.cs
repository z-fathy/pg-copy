using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;
using PgCopy.Core.Catalog;
using PgCopy.Core.Connections;
using PgCopy.Core.Planning;
using PgCopy.Core.PostData;
using PgCopy.Core.Schema;
using PgCopy.Core.Target;

namespace PgCopy.Core.Orchestration;

/// <summary>
/// Clones one database. The target is built under a temporary name and only swapped into place when
/// complete, so an existing local copy stays usable until the new one is ready.
/// </summary>
public sealed class CloneJob
{
    public Task<CloneReport> RunAsync(CloneRequest request, IProgress<CloneEvent>? progress, CancellationToken ct) =>
        new Run(request, progress ?? new Progress<CloneEvent>(_ => { })).ExecuteAsync(ct);

    private sealed class Run
    {
        private readonly CloneRequest _req;
        private readonly CloneOptions _o;
        private readonly IProgress<CloneEvent> _progress;
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly Stopwatch _phaseClock = new();
        private readonly Dictionary<ClonePhase, TimeSpan> _phaseTimes = [];
        private readonly List<string> _warnings = [];
        private ClonePhase? _phase;

        public Run(CloneRequest req, IProgress<CloneEvent> progress)
        {
            _req = req;
            _o = req.Options;
            _progress = progress;
        }

        private void Phase(ClonePhase phase)
        {
            if (_phase is { } previous)
                _phaseTimes[previous] = _phaseTimes.GetValueOrDefault(previous) + _phaseClock.Elapsed;
            _phase = phase;
            _phaseClock.Restart();
            _progress.Report(new PhaseEvent(phase));
        }

        private void ClosePhase()
        {
            if (_phase is { } current)
                _phaseTimes[current] = _phaseTimes.GetValueOrDefault(current) + _phaseClock.Elapsed;
            _phase = null;
        }

        private void Log(string message) => _progress.Report(new LogEvent(false, message));

        private void Warn(string message)
        {
            lock (_warnings)
                _warnings.Add(message);
            _progress.Report(new LogEvent(true, message));
        }

        public async Task<CloneReport> ExecuteAsync(CancellationToken ct)
        {
            var source = _req.Source.ForDatabase(_req.SourceDatabase);
            var staging = _req.TargetDatabase + "__pgcopy_tmp";
            var target = _req.Target;
            var swapped = false;
            NpgsqlConnection? coordinator = null;
            NpgsqlConnection? admin = null;
            var loadMode = false;

            try
            {
                // ---- Preflight: consistent snapshot + catalog ----
                Phase(ClonePhase.Preflight);
                coordinator = new NpgsqlConnection(source.ToConnectionString(ConnectionRole.Plain));
                await OpenWithContextAsync(coordinator, $"source {source}", ct);
                await DatabaseAdmin.ExecAsync(coordinator, "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY", ct);
                string? snapshotId = null;
                if (_o.UseSnapshot)
                {
                    try
                    {
                        snapshotId = await DatabaseAdmin.ScalarAsync<string>(coordinator, "select pg_export_snapshot()", ct);
                    }
                    catch (PostgresException ex)
                    {
                        throw new InvalidOperationException(
                            $"The source does not support exported snapshots ({ex.MessageText}). This usually means a transaction-pooling proxy or a read replica. Connect to the primary directly or disable the snapshot (inconsistent copy).",
                            ex
                        );
                    }
                }
                // Empty search_path makes pg_get_*def() emit fully qualified names.
                await DatabaseAdmin.ExecAsync(coordinator, "SET search_path = ''", ct);
                var catalog = await SourceCatalog.ReadAsync(coordinator, _o.ExcludeTableData, ct);
                var info = catalog.Info;
                Log(
                    $"Source: PostgreSQL {info.ServerVersion}, {Format(info.SizeBytes)}, {catalog.Tables.Count} tables to copy."
                );

                await target.PrepareAsync(info.Major, Log, ct);
                Log($"Target: {target.Name}.");
                admin = new NpgsqlConnection(target.Admin.ToConnectionString(ConnectionRole.Plain));
                await admin.OpenAsync(ct);
                await CheckPreconditionsAsync(admin, catalog, ct);

                var targetIsSuper = await DatabaseAdmin.ScalarAsync<bool>(
                    admin,
                    "select rolsuper from pg_roles where rolname = current_user",
                    ct
                );
                var indexJobs = _o.EffectiveIndexJobs(target.CpuCount);
                var maintenanceMb = (int)Math.Clamp(target.MemoryBytes / 2 / indexJobs >> 20, 64, 2048);

                await target.EnterLoadModeAsync(ct);
                loadMode = true;

                // ---- Staging database ----
                Phase(ClonePhase.CreateDatabase);
                await DatabaseAdmin.DropIfExistsAsync(admin, staging, ct);
                if (!await CreateDatabaseAsync(admin, staging, info, ct))
                    Warn(
                        $"Locale '{info.Collate}' is not available on the target; using the server default (sort order may differ)."
                    );
                await CreatePolicyRolesAsync(admin, catalog.PolicyRoles, ct);
                var stagingProfile = target.Admin.ForDatabase(staging);

                // ---- Schema (pre-data) ----
                Phase(ClonePhase.Schema);
                await using var host = await CreateToolHostAsync(info.Major, ct);
                Log($"Using {host.Description}.");
                var tools = new SchemaTools(host);
                await tools.DumpAsync(source, snapshotId, "schema.dump", largeObjectsOnly: false, ct);
                var toc = await tools.ListAsync("schema.dump", ct);
                await tools.WriteListAsync("pre.list", toc.Render(e => TocList.IsType(e, "USER MAPPING")), ct);
                var pre = await tools.RestoreAsync("schema.dump", stagingProfile, staging, "pre-data", "pre.list", 1, ct);
                CheckRestore(pre, "pre-data");

                // ---- Data copy, with indexes building behind it ----
                Phase(ClonePhase.DataCopy);
                var tasks = ChunkPlanner.Plan(catalog.Tables, _o.SplitSizeBytes, _o.UseBinary, catalog.BinarySafeTypes);
                var tracker = new ProgressTracker();
                tracker.Register(catalog.Tables.Select(t => t.DisplayName));
                var plans = catalog
                    .Tables.Select(t => new TablePlan(t.DisplayName, t.Bytes, tasks.Count(x => x.Table.Oid == t.Oid)))
                    .ToList();
                var earlyEnabled = _o.EarlyIndexes;
                _progress.Report(new PlanEvent(plans, earlyEnabled ? catalog.EarlyIndexes.Count : 0));

                var builtIndexes = new ConcurrentBag<IndexDefinition>();
                var analyzed = new ConcurrentDictionary<string, bool>();
                var indexesByTable = catalog
                    .EarlyIndexes.GroupBy(i => i.TableOid)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<IndexDefinition>)g.ToList());
                using var tickerCts = new CancellationTokenSource();
                var ticker = tracker.RunTickerAsync(_progress, tickerCts.Token);
                using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                EarlyIndexBuilder? builder = null;
                if (earlyEnabled)
                {
                    builder = new EarlyIndexBuilder(
                        stagingProfile,
                        indexJobs,
                        maintenanceMb,
                        builtIndexes.Add,
                        Warn,
                        name => analyzed[name] = true,
                        workCts.Token
                    );
                    tracker.BindIndexCounter(() => builder.Completed);
                }

                // Large objects only need the snapshot, so they stream in parallel with the table copy.
                var largeObjects =
                    catalog.LargeObjectCount > 0
                        ? Task.Run(
                            async () =>
                            {
                                Log($"Copying {catalog.LargeObjectCount} large objects...");
                                await tools.DumpAsync(source, snapshotId, "lo.dump", largeObjectsOnly: true, workCts.Token);
                                // Keep only the large-object entries (their creation lives in pre-data, their contents in data).
                                var loToc = await tools.ListAsync("lo.dump", workCts.Token);
                                await tools.WriteListAsync("lo.list", loToc.Render(e => !IsLargeObjectEntry(e)), workCts.Token);
                                var lo = await tools.RestoreAsync(
                                    "lo.dump",
                                    stagingProfile,
                                    staging,
                                    null,
                                    "lo.list",
                                    1,
                                    workCts.Token
                                );
                                CheckRestore(lo, "large objects");
                            },
                            workCts.Token
                        )
                        : Task.CompletedTask;

                try
                {
                    await DataCopyStage.RunAsync(
                        tasks,
                        source,
                        stagingProfile,
                        snapshotId,
                        _o.Jobs,
                        tracker,
                        table =>
                        {
                            if (builder is not null && !table.IsPartition && !table.IsExtensionConfigTable)
                                builder.Enqueue(
                                    table.DisplayName,
                                    table.QualifiedName,
                                    indexesByTable.GetValueOrDefault(table.Oid) ?? []
                                );
                        },
                        Warn,
                        workCts.Token
                    );
                    Phase(ClonePhase.FinishIndexes);
                    if (builder is not null)
                        await builder.CompleteAsync();
                    await largeObjects;
                }
                catch
                {
                    await workCts.CancelAsync();
                    await Swallow(largeObjects);
                    if (builder is not null)
                        await Swallow(builder.CompleteAsync());
                    throw;
                }
                finally
                {
                    await tickerCts.CancelAsync();
                    await ticker;
                }

                // ---- Foreign keys ----
                var builtFks = new List<uint>();
                var tableOids = catalog.Tables.Select(t => t.Oid).ToHashSet();
                if (_o.TrustedForeignKeys && earlyEnabled && targetIsSuper)
                {
                    Phase(ClonePhase.ForeignKeys);
                    await using var fkConn = new NpgsqlConnection(stagingProfile.ToConnectionString(ConnectionRole.Plain));
                    await fkConn.OpenAsync(ct);
                    builtFks.AddRange(await TrustedForeignKeyBuilder.AddAsync(fkConn, catalog.TrustedForeignKeys, Warn, ct));
                }

                // ---- Sequences ----
                Phase(ClonePhase.SequencesAndLargeObjects);
                var sequences = await SourceCatalog.ReadSequencesAsync(coordinator, ct);
                await using (var seqConn = new NpgsqlConnection(stagingProfile.ToConnectionString(ConnectionRole.Plain)))
                {
                    await seqConn.OpenAsync(ct);
                    await SequenceRestorer.RestoreAsync(seqConn, sequences, ct);
                }

                // ---- Post-data (whatever the early steps did not already create) ----
                Phase(ClonePhase.PostData);
                var done = new HashSet<(uint, uint)>();
                foreach (var index in builtIndexes)
                    done.Add(
                        index.ConstraintOid is { } c ? (TocList.PgConstraintTable, c) : (TocList.PgClassTable, index.IndexOid)
                    );
                foreach (var fk in builtFks)
                    done.Add((TocList.PgConstraintTable, fk));
                await tools.WriteListAsync(
                    "post.list",
                    toc.Render(e => TocList.IsType(e, "USER MAPPING") || done.Contains((e.CatalogTable, e.CatalogOid))),
                    ct
                );
                var post = await tools.RestoreAsync(
                    "schema.dump",
                    stagingProfile,
                    staging,
                    "post-data",
                    "post.list",
                    indexJobs,
                    ct
                );
                CheckRestore(post, "post-data");

                if (!_o.SkipMatviewRefresh && catalog.MaterializedViews.Count > 0)
                {
                    Log($"Refreshing {catalog.MaterializedViews.Count} materialized view(s)...");
                    await using var mvConn = new NpgsqlConnection(
                        stagingProfile.ToConnectionString(ConnectionRole.TargetMaintenance, maintenanceMb)
                    );
                    await mvConn.OpenAsync(ct);
                    foreach (var view in catalog.MaterializedViews)
                        await DatabaseAdmin.ExecAsync(mvConn, $"REFRESH MATERIALIZED VIEW {view}", ct);
                }

                // ---- Statistics for anything not analysed early ----
                Phase(ClonePhase.Analyze);
                await AnalyzeRemainingAsync(stagingProfile, analyzed, indexJobs, ct);

                // ---- Finalise ----
                Phase(ClonePhase.Swap);
                await ApplyDatabaseSettingsAsync(admin, staging, catalog.DatabaseSettings, ct);
                loadMode = false;
                await target.ExitLoadModeAsync(ct);
                await DatabaseAdmin.SwapAsync(admin, staging, _req.TargetDatabase, ct);
                swapped = true;

                ClosePhase();
                var report = new CloneReport(
                    _req.SourceDatabase,
                    _req.TargetDatabase,
                    _total.Elapsed,
                    new Dictionary<ClonePhase, TimeSpan>(_phaseTimes),
                    tracker.TotalBytes,
                    catalog.Tables.Count,
                    builtIndexes.Count,
                    tracker.AverageMBps,
                    tracker.PeakMBps,
                    _warnings.ToList()
                );
                return report;
            }
            finally
            {
                try
                {
                    if (coordinator is not null)
                        await coordinator.DisposeAsync();
                    if (!swapped && admin is not null)
                        await DatabaseAdmin.DropIfExistsAsync(admin, staging, CancellationToken.None);
                    if (loadMode)
                        await target.ExitLoadModeAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
                {
                    // Best-effort cleanup must not mask the original failure.
                }
                finally
                {
                    if (admin is not null)
                        await admin.DisposeAsync();
                }
            }
        }

        private async Task<IPgToolHost> CreateToolHostAsync(int major, CancellationToken ct)
        {
            var binDir = await PgToolsLocator.FindBinDirAsync(major, _o.PgBinDirectory, ct);
            if (binDir is not null)
                return new LocalPgToolHost(binDir);
            if (_req.Target.DockerContainer is { } container)
                return new DockerPgToolHost(container);
            throw new InvalidOperationException(
                $"pg_dump/pg_restore {major} or newer were not found. Install the PostgreSQL client tools or pass --pg-bin."
            );
        }

        private static async Task OpenWithContextAsync(NpgsqlConnection conn, string what, CancellationToken ct)
        {
            try
            {
                await conn.OpenAsync(ct);
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
            {
                throw new InvalidOperationException($"Cannot connect to {what}: {ex.Message}", ex);
            }
        }

        private async Task CheckPreconditionsAsync(NpgsqlConnection admin, SourceCatalogSnapshot catalog, CancellationToken ct)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new NpgsqlCommand("select name from pg_available_extensions", admin))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    available.Add(r.GetString(0));
            }
            var missing = catalog.Extensions.Where(e => !available.Contains(e)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"The target server lacks these extensions used by the source: {string.Join(", ", missing)}. "
                        + "Use a Docker image that bundles them (e.g. postgis/postgis, pgvector/pgvector) via --image."
                );

            var copied = catalog.Tables.Select(t => t.DisplayName).ToHashSet();
            var blocked = catalog.UnreadableTables.Concat(catalog.RlsTables).Where(copied.Contains).Distinct().ToList();
            if (blocked.Count > 0)
                throw new InvalidOperationException(
                    $"The source user cannot read all rows of: {string.Join(", ", blocked.Take(10))}{(blocked.Count > 10 ? ", ..." : "")}. "
                        + "Use a user with SELECT (and BYPASSRLS for row-level security), or skip them with --exclude-table-data."
                );
        }

        private static async Task<bool> CreateDatabaseAsync(
            NpgsqlConnection admin,
            string name,
            SourceInfo info,
            CancellationToken ct
        )
        {
            var sql =
                $"CREATE DATABASE {DatabaseAdmin.Ident(name)} TEMPLATE template0 ENCODING {DatabaseAdmin.Literal(info.Encoding)} "
                + $"LC_COLLATE {DatabaseAdmin.Literal(info.Collate)} LC_CTYPE {DatabaseAdmin.Literal(info.Ctype)}";
            if (info.LocaleProvider == "i" && info.IcuLocale is not null)
                sql += $" LOCALE_PROVIDER icu ICU_LOCALE {DatabaseAdmin.Literal(info.IcuLocale)}";
            try
            {
                await DatabaseAdmin.ExecAsync(admin, sql, ct);
                return true;
            }
            catch (PostgresException)
            {
                // The source locale does not exist on the target OS (common for libc names across platforms).
                await DatabaseAdmin.ExecAsync(
                    admin,
                    $"CREATE DATABASE {DatabaseAdmin.Ident(name)} TEMPLATE template0 ENCODING {DatabaseAdmin.Literal(info.Encoding)}",
                    ct
                );
                return false;
            }
        }

        private async Task CreatePolicyRolesAsync(NpgsqlConnection admin, IReadOnlyList<string> roles, CancellationToken ct)
        {
            foreach (var role in roles)
            {
                if (
                    await DatabaseAdmin.ScalarAsync<int>(
                        admin,
                        $"select count(*) from pg_roles where rolname = {DatabaseAdmin.Literal(role)}",
                        ct
                    ) == 0
                )
                {
                    await DatabaseAdmin.ExecAsync(admin, $"CREATE ROLE {DatabaseAdmin.Ident(role)} NOLOGIN", ct);
                    Log($"Created placeholder role {role} (referenced by row-level security policies).");
                }
            }
        }

        private void CheckRestore(RestoreResult result, string stage)
        {
            var errors = result.Errors.Where(e => !e.IsBenign).ToList();
            if (
                result.ExitCode != 0
                && errors.Count == 0
                && result.Errors.Count == 0
                && result.StdErr.Contains("pg_restore: error")
            )
                errors.Add(new RestoreError(result.StdErr.Trim(), null));
            if (errors.Count == 0)
                return;
            var text = string.Join(
                Environment.NewLine,
                errors.Take(10).Select(e => $"  - {e.Message}{(e.Command is null ? "" : $" [{Truncate(e.Command, 160)}]")}")
            );
            if (!_o.IgnoreRestoreErrors)
                throw new InvalidOperationException(
                    $"pg_restore ({stage}) reported {errors.Count} error(s):{Environment.NewLine}{text}"
                );
            foreach (var e in errors)
                Warn($"pg_restore ({stage}): {e.Message}");
        }

        private async Task AnalyzeRemainingAsync(
            ConnectionProfile stagingProfile,
            ConcurrentDictionary<string, bool> analyzed,
            int parallelism,
            CancellationToken ct
        )
        {
            var pending = new List<string>();
            await using (var conn = new NpgsqlConnection(stagingProfile.ToConnectionString(ConnectionRole.Plain)))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    select n.nspname, c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace
                    where c.relkind in ('r','p','m') and n.nspname not in ('pg_catalog','information_schema') and n.nspname not like 'pg\_toast%'
                    """,
                    conn
                );
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var quoted = TableInfo.Quote(r.GetString(0)) + "." + TableInfo.Quote(r.GetString(1));
                    if (!analyzed.ContainsKey(quoted))
                        pending.Add(quoted);
                }
            }
            if (pending.Count == 0)
                return;
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct },
                async (table, token) =>
                {
                    await using var conn = new NpgsqlConnection(stagingProfile.ToConnectionString(ConnectionRole.Plain));
                    await conn.OpenAsync(token);
                    try
                    {
                        await DatabaseAdmin.ExecAsync(conn, $"ANALYZE {table}", token);
                    }
                    catch (PostgresException ex)
                    {
                        Warn($"ANALYZE {table} failed: {ex.MessageText}");
                    }
                }
            );
        }

        private static async Task ApplyDatabaseSettingsAsync(
            NpgsqlConnection admin,
            string database,
            IReadOnlyList<(string Name, string[] Settings)> settings,
            CancellationToken ct
        )
        {
            // GUC_LIST_QUOTE parameters hold comma separated identifiers that must be re-quoted one by one.
            string[] listParams = ["search_path", "temp_tablespaces", "session_preload_libraries", "local_preload_libraries"];
            foreach (var (name, values) in settings)
            {
                var raw = values[0];
                var rendered = listParams.Contains(name, StringComparer.OrdinalIgnoreCase)
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
                    // Setting unknown on the target (e.g. an extension GUC); not worth failing the clone.
                }
            }
        }

        private static bool IsLargeObjectEntry(TocList.Entry e) =>
            TocList.IsType(e, "BLOB")
            || TocList.IsType(e, "BLOBS")
            || TocList.IsType(e, "BLOB METADATA")
            || e.Rest.Contains("LARGE OBJECT", StringComparison.Ordinal);

        private static async Task Swallow(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // The primary failure is already propagating.
            }
        }

        private static string Format(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0.0} MB";

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
    }
}
