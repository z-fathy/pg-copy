using PgCopy.Core.Connections;

namespace PgCopy.Core.Schema;

/// <summary>
/// Where pg_dump / pg_restore run. They are either local binaries or executed inside the managed Docker
/// container (which always ships the matching major version). The host also owns the scratch directory for
/// dump files and exposes the endpoints as the tools see them.
/// </summary>
public interface IPgToolHost : IAsyncDisposable
{
    string Description { get; }

    /// <summary>Rewrites an endpoint so the tools can reach it (e.g. localhost inside a container).</summary>
    ConnectionProfile SourceFor(ConnectionProfile source);

    ConnectionProfile TargetFor(ConnectionProfile target);

    /// <summary>Path of a scratch file as seen by the tools.</summary>
    string PathOf(string fileName);

    Task WriteFileAsync(string fileName, string content, CancellationToken ct);

    Task<ToolResult> RunAsync(
        string tool,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct
    );
}
