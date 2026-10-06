using Npgsql;
using PgCopy.Core.Connections;

namespace PgCopy.Core.Tests;

public class ConnectionProfileTests
{
    [Fact]
    public void ParsesPostgresUri()
    {
        var p = ConnectionProfile.Parse("postgres://app%40corp:p%3Ass@db.dev.internal:6543/tenant1?sslmode=require");
        Assert.Equal("db.dev.internal", p.Host);
        Assert.Equal(6543, p.Port);
        Assert.Equal("app@corp", p.Username);
        Assert.Equal("p:ss", p.Password);
        Assert.Equal("tenant1", p.Database);
        Assert.Equal("Require", p.SslMode);
    }

    [Fact]
    public void UriDefaultsPortAndDatabase()
    {
        var p = ConnectionProfile.Parse("postgresql://u@h");
        Assert.Equal(5432, p.Port);
        Assert.Equal("postgres", p.Database);
    }

    [Fact]
    public void ParsesKeyValueString()
    {
        var p = ConnectionProfile.Parse("Host=h;Port=5555;Username=u;Password=pw;Database=d");
        Assert.Equal((5555, "u", "pw", "d"), (p.Port, p.Username, p.Password, p.Database));
    }

    [Fact]
    public void StreamRolesDisablePoolingAndEnlargeBuffers()
    {
        var b = new NpgsqlConnectionStringBuilder(
            new ConnectionProfile { Host = "h" }.ToConnectionString(ConnectionRole.SourceStream)
        );
        Assert.False(b.Pooling);
        Assert.Equal(0, b.CommandTimeout);
        Assert.Equal(1 << 20, b.ReadBufferSize);
        Assert.Contains("row_security=off", b.Options);
    }

    [Fact]
    public void PlainRoleSendsNoSessionOptions()
    {
        var b = new NpgsqlConnectionStringBuilder(
            new ConnectionProfile { Host = "h" }.ToConnectionString(ConnectionRole.Plain)
        );
        Assert.True(string.IsNullOrEmpty(b.Options));
    }

    [Fact]
    public void MaintenanceRoleSetsMaintenanceWorkMem()
    {
        var b = new NpgsqlConnectionStringBuilder(
            new ConnectionProfile { Host = "h" }.ToConnectionString(ConnectionRole.TargetMaintenance, 512)
        );
        Assert.Contains("maintenance_work_mem=512MB", b.Options);
    }
}
