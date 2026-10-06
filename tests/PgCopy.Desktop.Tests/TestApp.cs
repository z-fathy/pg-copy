using Avalonia;
using Avalonia.Headless;
using PgCopy.Desktop;

[assembly: AvaloniaTestApplication(typeof(PgCopy.Desktop.Tests.TestAppBuilder))]

namespace PgCopy.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
