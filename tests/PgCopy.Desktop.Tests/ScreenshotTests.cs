using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PgCopy.Core.Orchestration;
using PgCopy.Desktop.ViewModels;
using PgCopy.Desktop.Views;

namespace PgCopy.Desktop.Tests;

public class ScreenshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgcopy-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public ScreenshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private MainViewModel NewViewModel() => new(AppServices.CreateIsolated(_dir));

    private static void Capture(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("PGCOPY_SCREENSHOT_DIR");
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
#pragma warning disable CS0618 // the encoder-options overload has no stable equivalent for plain PNG yet
            frame.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
        }
    }

    [AvaloniaFact]
    public void MainWindowRendersAllTabs()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm };
        Capture(window, "01-clone-empty");

        var tabs = window.GetVisualDescendantsOfType<TabControl>().Single();
        for (var i = 1; i < 4; i++)
        {
            tabs.SelectedIndex = i;
            Capture(window, $"0{i + 1}-tab-{i}");
        }
    }

    [AvaloniaFact]
    public void CloneViewShowsDatabasesAndLiveProgress()
    {
        var vm = NewViewModel();
        vm.Clone.Source.Host = "dev-db.internal";
        vm.Clone.Profiles.Add("dev-main");
        foreach (
            var (name, size) in new[]
            {
                ("tenant_acme", 4_800_000_000L),
                ("tenant_globex", 1_250_000_000L),
                ("tenant_initech", 310_000_000L),
            }
        )
            vm.Clone.Databases.Add(new DatabaseItemViewModel(name, size) { IsSelected = name != "tenant_initech" });

        var clone = vm.Clone;
        InvokeApply(clone, new PhaseEvent(ClonePhase.DataCopy));
        InvokeApply(
            clone,
            new PlanEvent(
                [
                    new TablePlan("public.orders", 2_400_000_000, 19),
                    new TablePlan("public.order_items", 1_100_000_000, 9),
                    new TablePlan("billing.invoices", 540_000_000, 5),
                    new TablePlan("public.customers", 64_000_000, 1),
                ],
                14
            )
        );
        InvokeApply(clone, new LogEvent(false, "Source: PostgreSQL 16.4, 4.5 GB, 212 tables to copy."));
        InvokeApply(clone, new LogEvent(true, "public.audit: binary COPY failed; retrying as text."));
        InvokeApply(
            clone,
            new ProgressEvent(
                1_900_000_000,
                148.2,
                96,
                212,
                31,
                [
                    new TableProgress("public.orders", 1_300_000_000, TableState.Copying),
                    new TableProgress("public.order_items", 700_000_000, TableState.Copying),
                    new TableProgress("billing.invoices", 540_000_000, TableState.Done),
                ]
            )
        );
        clone.DatabaseProgress = "Database 1 of 2: tenant_acme";
        clone.IsRunning = true;

        var window = new MainWindow { DataContext = vm };
        Capture(window, "10-clone-progress");

        // Regression: CheckBox.Content treats '_' as an access-key marker and would render "tenantacme".
        var shown = window.GetVisualDescendantsOfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("tenant_acme", shown);
        Assert.Contains("tenant_globex", shown);

        Assert.Equal(3, clone.Databases.Count);
        Assert.Equal(4, clone.Tables.Count);
        Assert.InRange(clone.OverallPercent, 30, 50);
        Assert.Equal("done", clone.Tables.Single(t => t.Name == "billing.invoices").Status);
        Assert.Contains("MB/s", clone.StatsText);
    }

    private static void InvokeApply(CloneViewModel vm, CloneEvent e) =>
        typeof(CloneViewModel)
            .GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [e]);
}
