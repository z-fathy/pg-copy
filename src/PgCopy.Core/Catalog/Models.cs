namespace PgCopy.Core.Catalog;

public sealed record DatabaseListing(string Name, long SizeBytes);

public sealed record SourceInfo(
    int ServerVersionNum,
    string ServerVersion,
    string Encoding,
    string Collate,
    string Ctype,
    string LocaleProvider,
    string? IcuLocale,
    long SizeBytes,
    bool IsSuperuser
)
{
    public int Major => ServerVersionNum / 10000;
}

public sealed record ColumnInfo(string Name, uint TypeOid);

/// <summary>A leaf relation whose rows must be copied.</summary>
public sealed record TableInfo(
    uint Oid,
    string Schema,
    string Name,
    long Bytes,
    long Pages,
    bool IsPartition,
    string? ExtensionCondition,
    bool IsExtensionConfigTable,
    IReadOnlyList<ColumnInfo> Columns
)
{
    public string QualifiedName => $"{Quote(Schema)}.{Quote(Name)}";
    public string DisplayName => $"{Schema}.{Name}";

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string ColumnList => string.Join(", ", Columns.Select(c => Quote(c.Name)));
}

/// <summary>A constraint or index that can be built right after its table's data lands.</summary>
public sealed record IndexDefinition(uint TableOid, uint IndexOid, uint? ConstraintOid, string Name, string Sql);

public sealed record ForeignKeyDefinition(uint ConstraintOid, string TableQualified, string ConstraintName, string Definition);

public sealed record SequenceValue(string Schema, string Name, long LastValue);

public sealed record SourceCatalogSnapshot(
    SourceInfo Info,
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<IndexDefinition> EarlyIndexes,
    IReadOnlyList<ForeignKeyDefinition> TrustedForeignKeys,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> PolicyRoles,
    IReadOnlyList<(string Name, string Value)> DatabaseSettings,
    long LargeObjectCount,
    IReadOnlyList<string> RlsTables,
    IReadOnlyList<string> UnreadableTables,
    IReadOnlySet<uint> BinarySafeTypes,
    IReadOnlyList<string> MaterializedViews
);
