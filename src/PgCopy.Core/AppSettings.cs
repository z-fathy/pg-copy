using System.Text.Json;

namespace PgCopy.Core;

/// <summary>User preferences persisted between runs (never contains secrets).</summary>
public sealed record AppSettings
{
    public string? PgBinDirectory { get; init; }
    public string? DockerImage { get; init; }
    public string? LastSourceProfile { get; init; }
    public string TargetMode { get; init; } = "Docker";
    public string ExistingHost { get; init; } = "localhost";
    public int ExistingPort { get; init; } = 5432;
    public string ExistingUser { get; init; } = "postgres";
    public int DockerMajor { get; init; } = 16;
    public int? Jobs { get; init; }
    public string SplitSize { get; init; } = "128MB";
}

public sealed class AppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore(string? path = null) => _path = path ?? Path.Combine(AppPaths.DataDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
