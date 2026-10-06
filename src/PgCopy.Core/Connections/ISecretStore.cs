using System.Security.Cryptography;
using System.Text;

namespace PgCopy.Core.Connections;

public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}

/// <summary>
/// File-backed secret store under the user's local app data. Values are protected with DPAPI
/// (current user scope) on Windows; on other platforms the file is created owner-only.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileSecretStore(string? path = null) => _path = path ?? Path.Combine(AppPaths.DataDirectory, "secrets.json");

    public string? Get(string key)
    {
        lock (_gate)
            return Load().TryGetValue(key, out var v) ? Unprotect(v) : null;
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var map = Load();
            map[key] = Protect(value);
            Save(map);
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            var map = Load();
            if (map.Remove(key))
                Save(map);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(_path)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var fs = new FileStream(_path, options);
        System.Text.Json.JsonSerializer.Serialize(fs, map);
    }

    private static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return OperatingSystem.IsWindows()
            ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser))
            : "plain:" + Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser)
                );
            if (stored.StartsWith("plain:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        }
        catch (CryptographicException) { }
        return null;
    }
}
