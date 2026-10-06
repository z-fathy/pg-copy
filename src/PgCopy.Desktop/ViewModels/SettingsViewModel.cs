using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgCopy.Core;

namespace PgCopy.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        var s = services.SettingsStore.Load();
        PgBinDirectory = s.PgBinDirectory ?? "";
        DockerImage = s.DockerImage ?? "";
    }

    [ObservableProperty]
    private string _pgBinDirectory = "";

    [ObservableProperty]
    private string _dockerImage = "";

    [ObservableProperty]
    private string _message = "";

    public string DataDirectory => AppPaths.DataDirectory;
    public string LogDirectory => AppPaths.LogDirectory;

    [RelayCommand]
    private void Save()
    {
        var s = _services.SettingsStore.Load();
        _services.SettingsStore.Save(
            s with
            {
                PgBinDirectory = string.IsNullOrWhiteSpace(PgBinDirectory) ? null : PgBinDirectory.Trim(),
                DockerImage = string.IsNullOrWhiteSpace(DockerImage) ? null : DockerImage.Trim(),
            }
        );
        Message = "Settings saved.";
    }
}
