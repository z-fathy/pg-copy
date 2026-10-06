using System.Text;
using System.Text.RegularExpressions;

namespace PgCopy.Core.Schema;

/// <summary>A parsed <c>pg_restore -l</c> table of contents that can be re-rendered with entries disabled.</summary>
public sealed partial class TocList
{
    public const uint PgClassTable = 1259;
    public const uint PgConstraintTable = 2606;

    // pg_dump entry descriptions made of several words; everything else is a single word.
    private static readonly string[] MultiWordTypes =
    [
        "MATERIALIZED VIEW DATA",
        "MATERIALIZED VIEW",
        "TABLE DATA",
        "TABLE ATTACH",
        "INDEX ATTACH",
        "SEQUENCE OWNED BY",
        "SEQUENCE SET",
        "FK CONSTRAINT",
        "CHECK CONSTRAINT",
        "USER MAPPING",
        "FOREIGN TABLE",
        "FOREIGN DATA WRAPPER",
        "EVENT TRIGGER",
        "ROW SECURITY",
        "DEFAULT ACL",
        "SHELL TYPE",
        "BLOB METADATA",
        "PUBLICATION TABLE",
        "PUBLICATION TABLES IN SCHEMA",
        "TEXT SEARCH CONFIGURATION",
        "TEXT SEARCH DICTIONARY",
        "TEXT SEARCH PARSER",
        "TEXT SEARCH TEMPLATE",
        "OPERATOR CLASS",
        "OPERATOR FAMILY",
        "STATISTICS DATA",
        "ACCESS METHOD",
        "DEFAULT",
    ];

    /// <param name="Rest">Everything after the catalog ids: type, schema, name, owner.</param>
    public sealed record Entry(string Raw, uint CatalogTable, uint CatalogOid, string Rest)
    {
        /// <summary>The entry type, e.g. <c>INDEX</c>, <c>INDEX ATTACH</c> or <c>MATERIALIZED VIEW DATA</c>.</summary>
        public string Type { get; } =
            MultiWordTypes.FirstOrDefault(t => Rest.StartsWith(t + " ", StringComparison.Ordinal) || Rest == t)
            ?? Rest.Split(' ', 2)[0];
    }

    private readonly List<(string Line, Entry? Entry)> _lines;

    private TocList(List<(string, Entry?)> lines) => _lines = lines;

    public IEnumerable<Entry> Entries => _lines.Where(l => l.Entry is not null).Select(l => l.Entry!);

    public static TocList Parse(string text)
    {
        var lines = new List<(string, Entry?)>();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var m = EntryPattern().Match(line);
            lines.Add(
                (
                    line,
                    m.Success
                        ? new Entry(line, uint.Parse(m.Groups[2].Value), uint.Parse(m.Groups[3].Value), m.Groups[4].Value)
                        : null
                )
            );
        }
        return new TocList(lines);
    }

    /// <summary>Renders the list, commenting out (with ';') every entry for which <paramref name="disable"/> is true.</summary>
    public string Render(Func<Entry, bool> disable)
    {
        var sb = new StringBuilder();
        foreach (var (line, entry) in _lines)
            sb.Append(entry is not null && disable(entry) ? ";" + line : line).Append('\n');
        return sb.ToString();
    }

    public static bool IsType(Entry entry, string type) => entry.Type == type;

    [GeneratedRegex(@"^(\d+); (\d+) (\d+) (.+)$")]
    private static partial Regex EntryPattern();
}
