using PgCopy.Core.Connections;

namespace PgCopy.Core.Target;

/// <summary>The local PostgreSQL server that receives the clone.</summary>
public interface ITargetServer
{
    string Name { get; }

    /// <summary>Connection to the maintenance database; valid after <see cref="PrepareAsync"/>.</summary>
    ConnectionProfile Admin { get; }

    /// <summary>Name of the managed container, when the target is Docker (enables running the client tools in it).</summary>
    string? DockerContainer { get; }

    long MemoryBytes { get; }

    int CpuCount { get; }

    /// <summary>Starts/validates the server for a source of the given major version.</summary>
    Task PrepareAsync(int sourceMajor, Action<string> log, CancellationToken ct);

    /// <summary>Relaxes durability for the duration of a clone (managed servers only).</summary>
    Task EnterLoadModeAsync(CancellationToken ct);

    Task ExitLoadModeAsync(CancellationToken ct);
}
