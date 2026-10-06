using System.Text;
using System.Text.RegularExpressions;

namespace PgCopy.Core.Schema;

/// <summary>A parsed <c>pg_restore -l</c> table of contents that can be re-rendered with entries disabled.</summary>
public sealed partial class TocList
{
    public const uint PgClassTable = 1259;
    public const uint PgConstraintTable = 2606;

    public sealed record Entry(string Raw, uint CatalogTable, uint CatalogOid, string Rest);

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

    public static bool IsType(Entry entry, string type) =>
        entry.Rest.StartsWith(type + " ", StringComparison.Ordinal) || entry.Rest == type;

    [GeneratedRegex(@"^(\d+); (\d+) (\d+) (.+)$")]
    private static partial Regex EntryPattern();
}
