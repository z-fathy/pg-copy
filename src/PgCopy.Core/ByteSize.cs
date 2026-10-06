using System.Globalization;
using System.Text.RegularExpressions;

namespace PgCopy.Core;

public static partial class ByteSize
{
    /// <summary>Parses "512MB", "1GB", "2.5 GiB" or a plain byte count. "0"/"off" yields 0.</summary>
    public static long Parse(string text)
    {
        text = text.Trim();
        if (text.Equals("off", StringComparison.OrdinalIgnoreCase))
            return 0;
        var m = Pattern().Match(text);
        if (!m.Success)
            throw new FormatException($"'{text}' is not a size such as 512MB or 1GB.");
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Value.ToUpperInvariant().TrimEnd('B', 'I');
        var factor = unit switch
        {
            "" => 1L,
            "K" => 1L << 10,
            "M" => 1L << 20,
            "G" => 1L << 30,
            "T" => 1L << 40,
            _ => throw new FormatException($"Unknown size unit in '{text}'."),
        };
        return (long)(value * factor);
    }

    public static string Format(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
            >= 1L << 10 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} B",
        };

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([KMGT]?i?B?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
