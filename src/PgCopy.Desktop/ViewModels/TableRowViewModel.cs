using CommunityToolkit.Mvvm.ComponentModel;
using PgCopy.Core;
using PgCopy.Core.Orchestration;

namespace PgCopy.Desktop.ViewModels;

public sealed partial class TableRowViewModel(string name, long estimatedBytes, int chunks) : ViewModelBase
{
    public string Name { get; } = name;
    public long EstimatedBytes { get; } = estimatedBytes;
    public string Detail { get; } =
        chunks > 1 ? $"{ByteSize.Format(estimatedBytes)} in {chunks} parts" : ByteSize.Format(estimatedBytes);

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _status = "waiting";

    public void Update(TableProgress p)
    {
        Status = p.State switch
        {
            TableState.Done => "done",
            TableState.Copying => "copying",
            _ => "waiting",
        };
        Percent =
            p.State == TableState.Done ? 100
            : EstimatedBytes > 0 ? Math.Min(99, p.BytesDone * 100.0 / EstimatedBytes)
            : 0;
    }
}
