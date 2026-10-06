using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PgCopy.Core;
using PgCopy.Core.Orchestration;

namespace PgCopy.Desktop.ViewModels;

public sealed partial class HistoryViewModel(AppServices services) : ViewModelBase
{
    public ObservableCollection<HistoryRow> Runs { get; } = [];

    [RelayCommand]
    public void Refresh()
    {
        Runs.Clear();
        foreach (var r in services.History.Load())
            Runs.Add(new HistoryRow(r));
    }
}

public sealed class HistoryRow(RunRecord record)
{
    public string When { get; } = record.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Title { get; } =
        record.SourceDatabase == record.TargetDatabase
            ? record.SourceDatabase
            : $"{record.SourceDatabase} → {record.TargetDatabase}";
    public string Endpoint { get; } = record.Source;
    public string Outcome { get; } = record.Success ? "Succeeded" : "Failed";
    public bool Success { get; } = record.Success;

    public string Summary { get; } =
        record.Success
            ? $"{record.TotalSeconds:0.0}s · {ByteSize.Format(record.Bytes)} · avg {record.AverageMBps:0} MB/s · peak {record.PeakMBps:0} MB/s · {record.Tables} tables"
            : record.Error ?? "";

    public string Phases { get; } =
        string.Join("   ", record.PhaseSeconds.Where(p => p.Value >= 0.05).Select(p => $"{p.Key} {p.Value:0.0}s"));
}
