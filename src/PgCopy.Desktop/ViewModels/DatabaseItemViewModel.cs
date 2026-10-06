using CommunityToolkit.Mvvm.ComponentModel;
using PgCopy.Core;

namespace PgCopy.Desktop.ViewModels;

public sealed partial class DatabaseItemViewModel(string name, long sizeBytes) : ViewModelBase
{
    public string Name { get; } = name;
    public long SizeBytes { get; } = sizeBytes;
    public string SizeText { get; } = ByteSize.Format(sizeBytes);

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Name of the local copy; defaults to the source name.</summary>
    [ObservableProperty]
    private string _localName = name;
}
