using Npgsql;
using PgCopy.Core.Orchestration;
using PgCopy.Core.Snapshots;
using PgCopy.Core.Target;

namespace PgCopy.IntegrationTests;

public sealed class CloneIntegrationTests(ServerFixture servers) : IClassFixture<ServerFixture>
{
    // Tiny split size so the small zoo tables are exercised through the ctid-range path as well.
    private static CloneOptions Options(Func<CloneOptions, CloneOptions>? tweak = null)
    {
        var options = new CloneOptions { Jobs = 4, SplitSizeBytes = 64 * 1024 };
        return tweak?.Invoke(options) ?? options;
    }

    private Task<CloneReport> CloneAsync(string db, string target, CloneOptions options, CancellationToken ct = default) =>
        new CloneJob().RunAsync(
            new CloneRequest(
                servers.Source,
                db,
                target,
                new ExistingTargetServer(servers.Target.ForDatabase("postgres")),
                options
            ),
            null,
            ct
        );

    private async Task AssertIdenticalAsync(string sourceDb, string targetDb)
    {
        var (exit, output) = await CompareHelper.RunAsync(servers.Source, servers.Target, sourceDb, targetDb);
        Assert.True(exit == 0, output);
    }

    [ItFact]
    public async Task ZooCloneIsIdenticalToSource()
    {
        var db = await servers.CreateZooAsync();
        try
        {
            var report = await CloneAsync(db, db, Options());

            Assert.True(report.Tables >= 15);
            Assert.True(report.BytesTransferred > 0);
            await AssertIdenticalAsync(db, db);
        }
        finally
        {
            await ServerFixture.DropAsync(servers.Source, db);
            await ServerFixture.DropAsync(servers.Target, db);
        }
    }

    [ItTheory]
    [InlineData("text-only", false, true, true)]
    [InlineData("no-early-indexes", true, false, true)]
    [InlineData("validated-fks", true, true, false)]
    public async Task OptionVariantsProduceIdenticalResults(string name, bool binary, bool early, bool trustedFks)
    {
        var db = await servers.CreateZooAsync();
        var local = $"{db}_{name}";
        try
        {
            await CloneAsync(
                db,
                local,
                Options(o => o with { UseBinary = binary, EarlyIndexes = early, TrustedForeignKeys = trustedFks })
            );
            await AssertIdenticalAsync(db, local);
        }
        finally
        {
            await ServerFixture.DropAsync(servers.Source, db);
            await ServerFixture.DropAsync(servers.Target, local);
        }
    }

    [ItFact]
    public async Task RecloneReplacesTheLocalCopyAndLeavesNoStagingDatabases()
    {
        var db = await servers.CreateZooAsync();
        try
        {
            await CloneAsync(db, db, Options());
            await ServerFixture.ExecAsync(servers.Target, db, "DELETE FROM sales.orders; DROP TABLE sales.scratch");

            await CloneAsync(db, db, Options());

            await AssertIdenticalAsync(db, db);
            var leftovers = await ServerFixture.ScalarAsync<long>(
                servers.Target,
                "postgres",
                "select count(*) from pg_database where datname like '%\\_\\_pgcopy\\_%'"
            );
            Assert.Equal(0, leftovers);
        }
        finally
        {
            await ServerFixture.DropAsync(servers.Source, db);
            await ServerFixture.DropAsync(servers.Target, db);
        }
    }

    [ItFact]
    public async Task CancelledCloneLeavesExistingLocalCopyUntouched()
    {
        var db = await servers.CreateZooAsync();
        try
        {
            await CloneAsync(db, db, Options());
            await ServerFixture.ExecAsync(servers.Target, db, "UPDATE sales.customers SET full_name = 'MARKER' WHERE id = 1");

            using var cts = new CancellationTokenSource();
            var progress = new Progress<CloneEvent>(e =>
            {
                if (e is PhaseEvent { Phase: ClonePhase.DataCopy })
                    cts.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new CloneJob().RunAsync(
                    new CloneRequest(
                        servers.Source,
                        db,
                        db,
                        new ExistingTargetServer(servers.Target.ForDatabase("postgres")),
                        Options()
                    ),
                    progress,
                    cts.Token
                )
            );

            Assert.Equal(
                "MARKER",
                await ServerFixture.ScalarAsync<string>(
                    servers.Target,
                    db,
                    "select full_name from sales.customers where id = 1"
                )
            );
            Assert.Equal(
                0,
                await ServerFixture.ScalarAsync<long>(
                    servers.Target,
                    "postgres",
                    "select count(*) from pg_database where datname like '%\\_\\_pgcopy\\_%'"
                )
            );
        }
        finally
        {
            await ServerFixture.DropAsync(servers.Source, db);
            await ServerFixture.DropAsync(servers.Target, db);
        }
    }

    [ItFact]
    public async Task SnapshotRestoreResetsDatabaseToSavedState()
    {
        var db = await servers.CreateZooAsync();
        var snapshots = new LocalSnapshotService(servers.Target.ForDatabase("postgres"));
        try
        {
            await CloneAsync(db, db, Options());
            await snapshots.SaveAsync(db, "fresh", default);
            Assert.Contains(await snapshots.ListAsync(db, default), s => s.Name == "fresh");

            await ServerFixture.ExecAsync(servers.Target, db, "DELETE FROM sales.orders");
            Assert.Equal(0, await ServerFixture.ScalarAsync<long>(servers.Target, db, "select count(*) from sales.orders"));

            await snapshots.RestoreAsync(db, "fresh", default);

            await AssertIdenticalAsync(db, db);
            await snapshots.DeleteAsync(db, "fresh", default);
            Assert.DoesNotContain(await snapshots.ListAsync(db, default), s => s.Name == "fresh");
        }
        finally
        {
            await ServerFixture.DropAsync(servers.Source, db);
            await ServerFixture.DropAsync(servers.Target, db, $"{db}__snap_fresh");
        }
    }

    [ItFact]
    public async Task UnreadableTablesFailFastWithGuidance()
    {
        var db = await servers.CreateZooAsync();
        try
        {
            await ServerFixture.ExecAsync(
                servers.Source,
                "postgres",
                "DROP ROLE IF EXISTS pgcopy_limited; CREATE ROLE pgcopy_limited LOGIN PASSWORD 'x'"
            );
            await ServerFixture.ExecAsync(
                servers.Source,
                db,
                "GRANT USAGE ON SCHEMA sales TO pgcopy_limited; GRANT SELECT ON sales.customers TO pgcopy_limited"
            );
            var limited = servers.Source with { Username = "pgcopy_limited", Password = "x" };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new CloneJob().RunAsync(
                    new CloneRequest(
                        limited,
                        db,
                        db,
                        new ExistingTargetServer(servers.Target.ForDatabase("postgres")),
                        Options()
                    ),
                    null,
                    default
                )
            );

            Assert.Contains("cannot read all rows", ex.Message);
            Assert.Contains("--exclude-table-data", ex.Message);
        }
        finally
        {
            await ServerFixture.DropAsync(servers.Source, db);
            await ServerFixture.DropAsync(servers.Target, db);
        }
    }
}
