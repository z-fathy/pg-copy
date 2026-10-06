using Avalonia;
using Avalonia.VisualTree;

namespace PgCopy.Desktop.Tests;

internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Visual visual)
        where T : Visual => visual.GetVisualDescendants().OfType<T>();
}
