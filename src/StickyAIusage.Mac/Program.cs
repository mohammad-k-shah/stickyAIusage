using Avalonia;
using StickyAIusage.Demo;
using StickyAIusage.Mac.Native;
using StickyAIusage.Services;

namespace StickyAIusage.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A release check, not a start: no lock, no menu bar item, no window, a temporary profile.
        if (SelfTest.IsRequested(args, out var reportPath))
        {
            Environment.ExitCode = SelfTestRunner.Run(reportPath);
            return;
        }

        // --demo: sample data in a temporary profile (before anything reads a path), removed on exit.
        using var demo = DemoMode.IsRequested(args, out var variant) ? DemoMode.Start(variant, DateTimeOffset.UtcNow) : null;
        if (demo is null)
            AppPaths.Configure(args);
        UnhandledErrors.Install();
        if (!SingleInstance.TryAcquire())
        {
            // Already running: ask that copy to open its flyout, then leave.
            FileSignal.Send(SingleInstance.SignalPath);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // A menu bar utility: no Dock icon and no app menu.
            .With(new MacOSPlatformOptions { ShowInDock = false, DisableDefaultApplicationMenuItems = true })
            .LogToTrace();
}
