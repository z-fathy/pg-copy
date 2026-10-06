using PgCopy.Core.Connections;
using PgCopy.Core.Target;

namespace PgCopy.Core.Orchestration;

public sealed record CloneRequest(
    ConnectionProfile Source,
    string SourceDatabase,
    string TargetDatabase,
    ITargetServer Target,
    CloneOptions Options
);
