namespace PgCopy.Core;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\pg-copy on Windows, ~/.local/share/pg-copy elsewhere.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pg-copy");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
