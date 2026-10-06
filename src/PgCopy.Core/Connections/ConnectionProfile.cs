using Npgsql;

namespace PgCopy.Core.Connections;

/// <summary>A PostgreSQL endpoint plus credentials. Immutable; use <c>with</c> to derive variants.</summary>
public sealed record ConnectionProfile
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Username { get; init; } = "postgres";
    public string? Password { get; init; }
    public string Database { get; init; } = "postgres";
    public string SslMode { get; init; } = "Prefer";

    /// <summary>Parses a libpq-style URI (postgres://u:p@h:5432/db?sslmode=require) or an Npgsql key=value string.</summary>
    public static ConnectionProfile Parse(string text)
    {
        if (
            text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        )
        {
            var uri = new Uri(text);
            var user = uri.UserInfo.Split(':', 2);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return new ConnectionProfile
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
                Username = user.Length > 0 && user[0].Length > 0 ? Uri.UnescapeDataString(user[0]) : "postgres",
                Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : null,
                Database = uri.AbsolutePath.Trim('/') is { Length: > 0 } d ? Uri.UnescapeDataString(d) : "postgres",
                SslMode = NormalizeSslMode(query["sslmode"]),
            };
        }

        var b = new NpgsqlConnectionStringBuilder(text);
        return new ConnectionProfile
        {
            Host = b.Host ?? "localhost",
            Port = b.Port,
            Username = b.Username ?? "postgres",
            Password = b.Password,
            Database = b.Database ?? "postgres",
            SslMode = b.SslMode.ToString(),
        };
    }

    private static string NormalizeSslMode(string? mode) =>
        mode?.ToLowerInvariant() switch
        {
            "disable" => "Disable",
            "allow" => "Allow",
            "require" => "Require",
            "verify-ca" => "VerifyCA",
            "verify-full" => "VerifyFull",
            _ => "Prefer",
        };

    public ConnectionProfile ForDatabase(string database) => this with { Database = database };

    /// <summary>
    /// Builds an Npgsql connection string tuned for bulk streaming: no pooling, large buffers,
    /// no timeouts, and session settings that make long-running COPYs safe.
    /// </summary>
    public string ToConnectionString(ConnectionRole role = ConnectionRole.Default, int maintenanceWorkMemMb = 0)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            Database = Database,
            SslMode = Enum.Parse<Npgsql.SslMode>(SslMode, ignoreCase: true),
            Pooling = false,
            Timeout = 30,
            CommandTimeout = 0,
            KeepAlive = 30,
            ApplicationName = "pg-copy",
        };

        var options = new List<string> { "-c statement_timeout=0", "-c idle_in_transaction_session_timeout=0" };
        switch (role)
        {
            case ConnectionRole.SourceStream:
                b.ReadBufferSize = 1 << 20;
                b.SocketReceiveBufferSize = 4 << 20;
                options.Add("-c row_security=off");
                break;
            case ConnectionRole.TargetStream:
                b.WriteBufferSize = 1 << 20;
                b.SocketSendBufferSize = 4 << 20;
                options.Add("-c synchronous_commit=off");
                options.Add("-c row_security=off");
                break;
            case ConnectionRole.TargetMaintenance:
                options.Add("-c synchronous_commit=off");
                if (maintenanceWorkMemMb > 0)
                    options.Add($"-c maintenance_work_mem={maintenanceWorkMemMb}MB");
                break;
            case ConnectionRole.Plain:
                options.Clear();
                break;
        }
        if (options.Count > 0)
            b.Options = string.Join(' ', options);
        return b.ConnectionString;
    }

    /// <summary>A libpq-style URI (credentials percent-encoded) that round-trips through <see cref="Parse"/>.</summary>
    public string ToUri(bool includePassword = true)
    {
        var user = Uri.EscapeDataString(Username);
        var credentials = includePassword && Password is not null ? $"{user}:{Uri.EscapeDataString(Password)}" : user;
        var ssl = SslMode switch
        {
            "VerifyCA" => "verify-ca",
            "VerifyFull" => "verify-full",
            var m => m.ToLowerInvariant(),
        };
        return $"postgres://{credentials}@{Host}:{Port}/{Uri.EscapeDataString(Database)}?sslmode={ssl}";
    }

    public override string ToString() => $"{Username}@{Host}:{Port}/{Database}";
}
