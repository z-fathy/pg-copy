using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Npgsql;
using PgCopy.Core;
using PgCopy.Core.Catalog;
using PgCopy.Core.Connections;
using PgCopy.Core.Orchestration;

namespace PgCopy.Desktop.ViewModels;

public sealed partial class CloneViewModel : ViewModelBase
{
    private static readonly string[] PhaseLabels =
    [
        "Checking source and target",
        "Creating local database",
        "Restoring schema",
        "Copying data",
        "Finishing indexes",
        "Adding foreign keys",
        "Sequences and large objects",
        "Post-data (triggers, policies, views)",
        "Updating statistics",
        "Swapping in the new copy",
    ];

    private readonly AppServices _services;
    private readonly Dictionary<string, TableRowViewModel> _rows = [];
    private long _totalEstimate;

    public CloneViewModel(AppServices services, TargetSettingsViewModel target)
    {
        _services = services;
        Target = target;
        var settings = services.SettingsStore.Load();
        Jobs = settings.Jobs ?? new CloneOptions().Jobs;
        SplitSize = settings.SplitSize;
        RefreshProfiles();
        if (settings.LastSourceProfile is { } last && Profiles.Contains(last))
            SelectedProfile = last;
    }

    public TargetSettingsViewModel Target { get; }
    public ConnectionFormViewModel Source { get; } = new();
    public ObservableCollection<string> Profiles { get; } = [];
    public ObservableCollection<DatabaseItemViewModel> Databases { get; } = [];
    public ObservableCollection<TableRowViewModel> Tables { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private string _profileName = "";

    // ---- options ----
    [ObservableProperty]
    private decimal? _jobs = 8;

    [ObservableProperty]
    private string _splitSize = "128MB";

    [ObservableProperty]
    private bool _useBinary = true;

    [ObservableProperty]
    private bool _earlyIndexes = true;

    [ObservableProperty]
    private bool _trustedForeignKeys = true;

    [ObservableProperty]
    private bool _safeLoad;

    [ObservableProperty]
    private bool _skipMatviewRefresh;

    [ObservableProperty]
    private string _excludeTableData = "";

    // ---- state ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(LoadDatabasesCommand))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    private string _statusMessage = "Enter the source connection and load its databases.";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private string _phaseText = "";

    [ObservableProperty]
    private double _overallPercent;

    [ObservableProperty]
    private string _statsText = "";

    [ObservableProperty]
    private string _etaText = "";

    [ObservableProperty]
    private string _databaseProgress = "";

    [ObservableProperty]
    private string _lastLogPath = "";

    /// <summary>Connection URI of the most recently replicated local database.</summary>
    [ObservableProperty]
    private string _localConnection = "";

    public bool HasSelection => Databases.Any(d => d.IsSelected);

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var p in _services.Profiles.List())
            Profiles.Add(p.Name);
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (value is not null && _services.Profiles.Load(value) is { } profile)
        {
            Source.Load(profile);
            ProfileName = value;
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            ErrorMessage = "Give the profile a name first.";
            return;
        }
        _services.Profiles.Save(ProfileName.Trim(), Source.ToProfile());
        RefreshProfiles();
        SelectedProfile = ProfileName.Trim();
        StatusMessage = $"Saved profile '{ProfileName.Trim()}'.";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is { } name && _services.Profiles.Remove(name))
        {
            RefreshProfiles();
            SelectedProfile = null;
            StatusMessage = $"Deleted profile '{name}'.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task LoadDatabasesAsync()
    {
        ErrorMessage = "";
        StatusMessage = "Connecting...";
        try
        {
            await using var conn = new NpgsqlConnection(Source.ToProfile().ToConnectionString(ConnectionRole.Plain));
            await conn.OpenAsync();
            var list = await SourceCatalog.ListDatabasesAsync(conn, CancellationToken.None);
            Databases.Clear();
            foreach (var db in list.Where(d => d.Name != "postgres"))
            {
                var item = new DatabaseItemViewModel(db.Name, db.SizeBytes);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DatabaseItemViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(HasSelection));
                        StartCommand.NotifyCanExecuteChanged();
                    }
                };
                Databases.Add(item);
            }
            StatusMessage = $"{Databases.Count} database(s) found. Tick the ones to replicate.";
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            ErrorMessage = "Could not connect: " + ex.Message;
            StatusMessage = "";
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var d in Databases)
            d.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var d in Databases)
            d.IsSelected = false;
    }

    private bool CanStart() => !IsRunning && HasSelection;

    private CloneOptions BuildOptions() =>
        new()
        {
            Jobs = (int)(Jobs ?? 8),
            SplitSizeBytes = ByteSize.Parse(SplitSize),
            UseBinary = UseBinary,
            EarlyIndexes = EarlyIndexes,
            TrustedForeignKeys = TrustedForeignKeys,
            SafeLoad = SafeLoad,
            SkipMatviewRefresh = SkipMatviewRefresh,
            ExcludeTableData = ExcludeTableData.Split(
                ['\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            ),
            PgBinDirectory = _services.SettingsStore.Load().PgBinDirectory,
        };

    [RelayCommand(CanExecute = nameof(CanStart), IncludeCancelCommand = true)]
    private async Task StartAsync(CancellationToken ct)
    {
        ErrorMessage = "";
        CloneOptions options;
        try
        {
            options = BuildOptions();
        }
        catch (FormatException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var selected = Databases.Where(d => d.IsSelected).ToList();
        PersistSettings();
        IsRunning = true;
        StartCommand.NotifyCanExecuteChanged();
        var failures = 0;
        try
        {
            var source = Source.ToProfile();
            var server = Target.CreateServer(SafeLoad);
            for (var i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var db = selected[i];
                DatabaseProgress = selected.Count > 1 ? $"Database {i + 1} of {selected.Count}: {db.Name}" : db.Name;
                if (!await CloneOneAsync(source, server, db, options, ct))
                    failures++;
            }
            StatusMessage =
                failures == 0 ? $"Done. {selected.Count} database(s) replicated." : $"Finished with {failures} failure(s).";
            OverallPercent = failures == 0 ? 100 : OverallPercent;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled. Any existing local copy was left untouched.";
            PhaseText = "";
        }
        finally
        {
            IsRunning = false;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<bool> CloneOneAsync(
        ConnectionProfile source,
        Core.Target.ITargetServer server,
        DatabaseItemViewModel db,
        CloneOptions options,
        CancellationToken ct
    )
    {
        Tables.Clear();
        _rows.Clear();
        Log.Clear();
        LocalConnection = "";
        OverallPercent = 0;
        StatsText = "";
        EtaText = "";
        var clock = Stopwatch.StartNew();
        using var fileLog = new FileLogProgress(db.Name);
        LastLogPath = fileLog.Path;
        var progress = new Progress<CloneEvent>(e =>
        {
            fileLog.Report(e);
            Apply(e);
        });

        try
        {
            var report = await new CloneJob().RunAsync(
                new CloneRequest(source, db.Name, db.LocalName.Trim(), server, options),
                progress,
                ct
            );
            _services.History.Append(RunRecord.FromReport(source.ToString(), report));
            LocalConnection = server.Admin.ForDatabase(db.LocalName.Trim()).ToUri();
            StatsText =
                $"{ByteSize.Format(report.BytesTransferred)} in {report.Total.TotalSeconds:0.0}s — avg {report.AverageMBps:0} MB/s, peak {report.PeakMBps:0} MB/s";
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is PostgresException pg ? pg.MessageText : ex.Message;
            fileLog.WriteLine("FAILED: " + ex);
            ErrorMessage = $"{db.Name}: {message}";
            _services.History.Append(RunRecord.FromFailure(source.ToString(), db.Name, db.LocalName, message, clock.Elapsed));
            return false;
        }
    }

    private void Apply(CloneEvent e)
    {
        switch (e)
        {
            case PhaseEvent p:
                PhaseText = $"Step {(int)p.Phase + 1}/{PhaseLabels.Length}: {PhaseLabels[(int)p.Phase]}";
                break;
            case LogEvent l:
                Log.Add((l.IsWarning ? "⚠ " : "") + l.Message);
                if (Log.Count > 300)
                    Log.RemoveAt(0);
                break;
            case PlanEvent plan:
                _totalEstimate = plan.Tables.Sum(t => t.EstimatedBytes);
                foreach (var t in plan.Tables)
                {
                    var row = new TableRowViewModel(t.Name, t.EstimatedBytes, t.Chunks);
                    _rows[t.Name] = row;
                    Tables.Add(row);
                }
                break;
            case ProgressEvent pr:
                foreach (var changed in pr.Changed)
                {
                    if (_rows.TryGetValue(changed.Name, out var row))
                        row.Update(changed);
                }
                // COPY traffic is a close, but not exact, proxy for relation size, so cap below 100 until done.
                OverallPercent = _totalEstimate > 0 ? Math.Min(99, pr.BytesTransferred * 100.0 / _totalEstimate) : 0;
                StatsText =
                    $"{ByteSize.Format(pr.BytesTransferred)} streamed · {pr.CurrentMBps:0} MB/s · tables {pr.TablesDone}/{pr.TablesTotal} · indexes built {pr.IndexesDone}";
                var remaining = _totalEstimate - pr.BytesTransferred;
                EtaText =
                    pr.CurrentMBps > 1 && remaining > 0
                        ? $"~{TimeSpan.FromSeconds(remaining / 1048576.0 / pr.CurrentMBps):mm\\:ss} left"
                        : "";
                break;
        }
    }

    private void PersistSettings()
    {
        Target.Persist();
        var s = _services.SettingsStore.Load();
        _services.SettingsStore.Save(
            s with
            {
                LastSourceProfile = SelectedProfile,
                Jobs = (int)(Jobs ?? 8),
                SplitSize = SplitSize,
            }
        );
    }
}
