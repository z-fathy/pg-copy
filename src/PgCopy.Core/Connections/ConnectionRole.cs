namespace PgCopy.Core.Connections;

public enum ConnectionRole
{
    /// <summary>Generic connection with long-running-safe timeouts.</summary>
    Default,

    /// <summary>Catalog-style connection with no session overrides (safe behind poolers that reject options).</summary>
    Plain,

    /// <summary>Source side of a COPY relay: big read buffer, RLS off so protected tables fail loudly.</summary>
    SourceStream,

    /// <summary>Target side of a COPY relay: big write buffer, async commit.</summary>
    TargetStream,

    /// <summary>Index builds / analyze on the target.</summary>
    TargetMaintenance,
}
