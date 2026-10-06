using Npgsql;
using PgCopy.Core.Connections;

namespace PgCopy.IntegrationTests;

/// <summary>
/// Integration tests run against two real servers supplied by the environment (a throwaway source and a
/// local target, e.g. two service containers in CI):
///   PGCOPY_IT_SOURCE=postgres://postgres:pw@127.0.0.1:5433/postgres
///   PGCOPY_IT_TARGET=postgres://postgres:pw@127.0.0.1:5434/postgres
/// When unset, every test is skipped.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public static bool Configured =>
        Environment.GetEnvironmentVariable("PGCOPY_IT_SOURCE") is not null
        && Environment.GetEnvironmentVariable("PGCOPY_IT_TARGET") is not null;

    public ConnectionProfile Source { get; private set; } = new();
    public ConnectionProfile Target { get; private set; } = new();

    public Task InitializeAsync()
    {
        if (Configured)
        {
            Source = ConnectionProfile.Parse(Environment.GetEnvironmentVariable("PGCOPY_IT_SOURCE")!);
            Target = ConnectionProfile.Parse(Environment.GetEnvironmentVariable("PGCOPY_IT_TARGET")!);
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static async Task ExecAsync(ConnectionProfile server, string database, string sql)
    {
        await using var conn = new NpgsqlConnection(server.ForDatabase(database).ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(ConnectionProfile server, string database, string sql)
    {
        await using var conn = new NpgsqlConnection(server.ForDatabase(database).ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public static async Task DropAsync(ConnectionProfile server, params string[] databases)
    {
        foreach (var db in databases)
            await ExecAsync(server, "postgres", $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
    }

    /// <summary>Creates a fresh uniquely named database on the source and seeds it with the schema zoo.</summary>
    public async Task<string> CreateZooAsync()
    {
        var name = "zoo_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync(Source, "postgres", $"CREATE DATABASE \"{name}\"");
        var script = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Seed", "zoo.sql"))).Replace(
            "__DB__",
            $"\"{name}\""
        );
        await ExecAsync(Source, name, script);
        return name;
    }
}
