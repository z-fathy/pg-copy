using System.Text.Json;

namespace PgCopy.Core.Connections;

public sealed record SavedProfile(string Name, string Host, int Port, string Username, string Database, string SslMode);

/// <summary>Named connection profiles. Metadata lives in profiles.json; passwords go to the secret store.</summary>
public sealed class ProfileStore
{
    private readonly string _path;
    private readonly ISecretStore _secrets;

    public ProfileStore(ISecretStore secrets, string? path = null)
    {
        _secrets = secrets;
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "profiles.json");
    }

    public IReadOnlyList<SavedProfile> List()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<SavedProfile>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(string name, ConnectionProfile profile)
    {
        var all = List().Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        all.Add(new SavedProfile(name, profile.Host, profile.Port, profile.Username, profile.Database, profile.SslMode));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(all.OrderBy(p => p.Name).ToList()));
        if (profile.Password is not null)
            _secrets.Set(SecretKey(name), profile.Password);
    }

    public bool Remove(string name)
    {
        var all = List().ToList();
        if (all.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
            return false;
        File.WriteAllText(_path, JsonSerializer.Serialize(all));
        _secrets.Remove(SecretKey(name));
        return true;
    }

    public ConnectionProfile? Load(string name)
    {
        var p = List().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return p is null
            ? null
            : new ConnectionProfile
            {
                Host = p.Host,
                Port = p.Port,
                Username = p.Username,
                Database = p.Database,
                SslMode = p.SslMode,
                Password = _secrets.Get(SecretKey(name)),
            };
    }

    private static string SecretKey(string name) => $"profile:{name}";
}
