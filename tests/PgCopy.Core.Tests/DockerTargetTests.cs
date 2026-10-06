using PgCopy.Core.Connections;
using PgCopy.Core.Schema;
using PgCopy.Core.Target;

namespace PgCopy.Core.Tests;

public class DockerTargetTests
{
    private static List<string> Args(int major = 16, string? image = null, long memory = 8L << 30, int cpus = 4) =>
        DockerTargetServer.BuildRunArgs("pgcopy-pg" + major, major, "secret", image, memory, cpus);

    [Fact]
    public void ContainerIsBoundToLoopbackWithNamedVolumeAndBulkLoadSettings()
    {
        var args = Args();

        Assert.Equal("127.0.0.1::5432", args[args.IndexOf("-p") + 1]); // loopback only, random host port
        Assert.Equal("pgcopy-pg16-data:/var/lib/postgresql/data", args[args.IndexOf("-v") + 1]); // volume, not an NTFS bind mount
        Assert.Contains("--shm-size=1g", args);
        Assert.Contains("wal_level=minimal", args); // required for COPY FREEZE to skip WAL
        Assert.Contains("max_wal_senders=0", args); // wal_level=minimal refuses to start otherwise
        Assert.Contains("POSTGRES_PASSWORD=secret", args);
        Assert.Equal("postgres:16", args[args.IndexOf("--add-host") + 2]);
    }

    [Fact]
    public void Postgres18MountsTheVolumeOneLevelHigher() =>
        Assert.Equal("pgcopy-pg18-data:/var/lib/postgresql", Args(18)[Args(18).IndexOf("-v") + 1]);

    [Fact]
    public void ImageOverrideIsUsedForExtensionHeavySources() =>
        Assert.Contains("postgis/postgis:16-3.4", Args(image: "postgis/postgis:16-3.4"));

    [Fact]
    public void MemorySettingsScaleWithDockerMemoryWithinBounds()
    {
        Assert.Contains("shared_buffers=2048MB", Args(memory: 8L << 30));
        Assert.Contains("maintenance_work_mem=1024MB", Args(memory: 8L << 30));
        Assert.Contains("shared_buffers=128MB", Args(memory: 256L << 20)); // floor
        Assert.Contains("shared_buffers=4096MB", Args(memory: 64L << 30)); // cap
        Assert.Contains("max_worker_processes=16", Args(cpus: 8));
    }

    [Theory]
    [InlineData("127.0.0.1:55432\n", 55432)]
    [InlineData("0.0.0.0:49153\n[::]:49153\n", 49153)]
    public void ParsesPublishedPort(string output, int expected) =>
        Assert.Equal(expected, DockerTargetServer.ParsePort(output));

    [Fact]
    public void ToolsInsideTheContainerReachTheServerLocallyAndLoopbackSourcesViaTheHost()
    {
        var host = new DockerPgToolHost("pgcopy-pg16");
        var target = host.TargetFor(
            new ConnectionProfile
            {
                Host = "127.0.0.1",
                Port = 55432,
                Username = "postgres",
            }
        );
        Assert.Equal(("localhost", 5432), (target.Host, target.Port));

        Assert.Equal("host.docker.internal", host.SourceFor(new ConnectionProfile { Host = "localhost" }).Host);
        Assert.Equal("db.dev.internal", host.SourceFor(new ConnectionProfile { Host = "db.dev.internal" }).Host);
        Assert.StartsWith("/tmp/pgcopy-", host.PathOf("schema.dump"));
    }
}
