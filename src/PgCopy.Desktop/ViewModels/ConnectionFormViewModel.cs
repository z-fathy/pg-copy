using CommunityToolkit.Mvvm.ComponentModel;
using PgCopy.Core.Connections;

namespace PgCopy.Desktop.ViewModels;

/// <summary>Editable host/port/user/password form that maps to a <see cref="ConnectionProfile"/>.</summary>
public sealed partial class ConnectionFormViewModel : ViewModelBase
{
    public static IReadOnlyList<string> SslModes { get; } = ["Prefer", "Require", "VerifyCA", "VerifyFull", "Disable"];

    [ObservableProperty]
    private string _host = "localhost";

    [ObservableProperty]
    private decimal? _port = 5432;

    [ObservableProperty]
    private string _username = "postgres";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _sslMode = "Prefer";

    public ConnectionProfile ToProfile(string database = "postgres") =>
        new()
        {
            Host = Host.Trim(),
            Port = (int)(Port ?? 5432),
            Username = Username.Trim(),
            Password = string.IsNullOrEmpty(Password) ? null : Password,
            SslMode = SslMode,
            Database = database,
        };

    public void Load(ConnectionProfile profile)
    {
        Host = profile.Host;
        Port = profile.Port;
        Username = profile.Username;
        Password = profile.Password ?? "";
        SslMode = profile.SslMode;
    }
}
