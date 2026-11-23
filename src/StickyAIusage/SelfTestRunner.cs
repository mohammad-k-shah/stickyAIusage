using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.ViewModels;

namespace StickyAIusage;

/// <summary>
/// <c>StickyAIusage.exe --self-test [report-path]</c> (see <see cref="SelfTest"/>):
/// the shared checks, then what only the Windows app has — the embedded tray
/// icon, both theme dictionaries, and the pill, flyout, Settings and the
/// customizer built and rendered off-screen with sample data. No window is
/// shown, no tray icon is created, nothing outside a temporary folder is read
/// or written, and no single-instance lock is taken, so it runs beside the
/// installed widget.
/// </summary>
internal static class SelfTestRunner
{
    /// <summary>Runs inside the started <see cref="App"/> (the dispatcher is needed for the
    /// windows) and returns the exit code.</summary>
    public static async Task<int> RunAsync(App app, string? reportPath)
    {
        using var sandbox = SelfTestSandbox.Create();
        var version = AppIdentity.Version(typeof(App).Assembly);
        var selfTest = new SelfTest($"Windows {version}");
        var output = Console.Out;
        using var watchdog = selfTest.StartWatchdog(SelfTest.TimeLimit, reportPath, output,
            code => Environment.Exit(code), sandbox.LogText);
        DiagnosticLog.WriteInfo($"Self-test of {version} in {sandbox.Root}.");
        // In a normal run the pill survives a UI-thread error (it is logged); here it fails the run.
        var uiErrors = new List<Exception>();
        app.DispatcherUnhandledException += (_, args) =>
        {
            lock (uiErrors)
                uiErrors.Add(args.Exception);
            args.Handled = true;
        };

        if (!selfTest.RunCoreChecks(sandbox, typeof(App).Assembly))
            return selfTest.Finish(reportPath, output, sandbox.LogText());
        var settings = SettingsService.Load();
        CheckTrayIcon(selfTest);
        CheckThemes(selfTest, app);
        await CheckFirstRefreshAsync(selfTest, settings);
        CheckScreens(selfTest, settings, DateTimeOffset.UtcNow);
        await Task.Delay(200); // let what the windows queued on the dispatcher run
        selfTest.Check("no UI-thread errors", () =>
        {
            lock (uiErrors)
                return uiErrors.Count == 0 ? "none" : throw new SelfTestFailure(string.Join("; ", uiErrors.Select(e => $"{e.GetType().Name}: {e.Message}")));
        });

        var exitCode = selfTest.Finish(reportPath, output, sandbox.LogText());
        // The windows' feed watchers hold the sandbox's folders: close them so it can be deleted.
        foreach (var window in app.Windows.Cast<Window>().ToList())
            window.Close();
        await Task.Delay(100);
        return exitCode;
    }

    /// <summary>The icon the tray shows comes from the exe itself, not the generic fallback.</summary>
    internal static void CheckTrayIcon(SelfTest selfTest)
    {
        selfTest.Check("tray icon", () =>
        {
            using var icon = App.LoadTrayIcon();
            using var large = App.LoadTrayIcon(large: true);
            if (icon.Handle == System.Drawing.SystemIcons.Application.Handle || large.Handle == System.Drawing.SystemIcons.Application.Handle)
                throw new SelfTestFailure($"{App.TrayIconResourceName} didn't load: the generic system icon would show");
            return $"{App.TrayIconResourceName} ({icon.Width}x{icon.Height}, toast {large.Width}x{large.Height})";
        });
    }

    /// <summary>Both palettes load and the app's styles find their colors in each.</summary>
    internal static void CheckThemes(SelfTest selfTest, Application app)
    {
        foreach (var dark in new[] { false, true })
        {
            var name = dark ? "Dark" : "Light";
            selfTest.Check($"theme {name.ToLowerInvariant()}", () =>
            {
                var tokens = new ResourceDictionary { Source = new Uri($"/StickyAIusage;component/Themes/Tokens.{name}.xaml", UriKind.Relative) };
                if (tokens.Count == 0)
                    throw new SelfTestFailure($"Tokens.{name}.xaml has no resources");
                ThemeService.ApplyForTests(dark);
                if (ThemeService.IsDark != dark || app.Resources.MergedDictionaries.Count == 0)
                    throw new SelfTestFailure($"the {name.ToLowerInvariant()} palette didn't apply");
                return $"Tokens.{name}.xaml, {tokens.Count} resources";
            });
        }
    }

    /// <summary>A real start's first refresh, in the sandbox: settings → usage source →
    /// the (empty) feed and transcripts, without a network request.</summary>
    private static async Task CheckFirstRefreshAsync(SelfTest selfTest, AppSettings settings)
    {
        DockedStripWindow? window = null;
        Task? refresh = null;
        selfTest.Check("first refresh started", () =>
        {
            window = new DockedStripWindow(settings);
            refresh = window.FirstRefresh;
            return $"usage source {settings.ClaudeSource}";
        });
        if (refresh is null)
            return;
        var finished = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(8))) == refresh;
        selfTest.Check("first refresh", () =>
        {
            if (!finished)
                throw new SelfTestFailure("still running after 8 s");
            refresh.GetAwaiter().GetResult(); // rethrows what it threw
            return "done without errors";
        });
        window?.Close();
    }

    /// <summary>The pill (both layouts, docked and free), the flyout with cards, Settings
    /// and the customizer: built from XAML, given sample data, measured and rendered.
    /// Rendering proves the templates, bindings and resources resolve; what the pixels
    /// show is the screenshot tests' job.</summary>
    internal static void CheckScreens(SelfTest selfTest, AppSettings settings, DateTimeOffset now)
    {
        selfTest.Check("pill", () =>
        {
            var plan = new DockedStripWindow(settings);
            plan.ApplyUsage(SamplePlanUsage(now), SampleSession(now));
            var monthly = new DockedStripWindow(settings);
            monthly.ApplyUsage(SampleMonthlyUsage(now), SampleSession(now), spentToday: 4.20m);
            monthly.SetCompact(true);
            return $"plan {Render(plan)}, monthly in the taskbar {Render(monthly)}";
        });
        selfTest.Check("flyout", () =>
        {
            var window = new DockedStripWindow(settings);
            window.ApplyUsage(SamplePlanUsage(now), SampleSession(now));
            window.ToggleExpanded();
            window.ShowSessions([SessionCardViewModel.From(SampleSession(now), now)]);
            return Render(window);
        });
        selfTest.Check("settings window", () =>
        {
            var window = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }), UsageMode.PlanLimits);
            var items = Health.HealthCheck.Build(SampleHealth(now));
            window.ShowHealth(new Health.HealthResult(items, Health.HealthCheck.Report(items, "self-test", now)));
            return $"{Render(window)} with {window.HealthItems.Count} health items";
        });
        selfTest.Check("first-run window", () => CheckOnboarding(settings, now));
        selfTest.Check("customizer", () => Render(new FieldsWindow(settings, () => { })));
        selfTest.Check("share card", () => CheckShareCards(settings, now));
    }

    /// <summary>The first-run window's three steps and the upgrade screen, laid out and drawn
    /// (never shown: the self-test opens no window). Nothing is installed: its Connect step
    /// has no command line to run.</summary>
    internal static string CheckOnboarding(AppSettings settings, DateTimeOffset now)
    {
        var sizes = new List<string>();
        foreach (var (screen, step) in new[]
                 {
                     (Health.OnboardingScreen.FirstRun, 1), (Health.OnboardingScreen.FirstRun, 2), (Health.OnboardingScreen.FirstRun, 3),
                     (Health.OnboardingScreen.WhatsNew, 2)
                 })
        {
            var connect = new Health.ConnectSession(() => SamplePlanUsage(now), () => false, new NoCommandLine(), null, Health.PluginInstaller.DefaultSource);
            var window = new OnboardingWindow(settings, screen, connect, new OnboardingActions(() => { }, _ => { }));
            window.ShowStep(step);
            sizes.Add(Render(window));
        }
        return "steps " + string.Join(", ", sizes.Take(3)) + "; upgrade " + sizes[3];
    }

    /// <summary>The self-test's Connect step: nothing to run.</summary>
    private sealed class NoCommandLine : Health.IClaudeCli
    {
        public Task<Health.CliRun> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new SelfTestFailure("the self-test must not start Claude Code");
    }

    private static Health.HealthFacts SampleHealth(DateTimeOffset now) => new()
    {
        Now = now, ClaudeDirectory = "~/.claude", ClaudeDirectoryExists = true, TranscriptCount = 2, ClaudeCodeVersion = "2.1.325",
        Marker = new Health.PluginMarker("2.3.0", "2.1.325", "terminal", now), NewestRecordAt = now, Usage = SamplePlanUsage(now)
    };

    /// <summary>Each card in its shape and theme, drawn and encoded as the Share menu does.</summary>
    internal static string CheckShareCards(AppSettings settings, DateTimeOffset now)
    {
        var portrait = new AppSettings { ShareCardShape = "portrait" };
        var cards = new[]
        {
            (Sharing.ShareCards.ForUsage(SamplePlanUsage(now), null, [SampleSession(now)], null, settings, now), true),
            (Sharing.ShareCards.ForUsage(SampleMonthlyUsage(now), null, [SampleSession(now)], 4.20m, portrait, now), false),
            (Sharing.ShareCards.ForSession(SampleSession(now), settings, now), false),
            (Sharing.ShareCards.ForYear(Sharing.YearCard.Sample(now.Year), portrait), true)
        };
        var sizes = new List<string>();
        foreach (var (card, dark) in cards)
        {
            if (card is null)
                throw new SelfTestFailure("a sample reading gave no card");
            var (width, height) = card.Shape.Size();
            var bitmap = ShareCardView.Render(card, dark);
            var png = Services.ShareService.Encode(bitmap);
            if (bitmap.PixelWidth != width || bitmap.PixelHeight != height || png.Length < 1000)
                throw new SelfTestFailure($"{card.Variant} drew {bitmap.PixelWidth}x{bitmap.PixelHeight}, {png.Length} bytes");
            sizes.Add($"{card.Variant.ToString().ToLowerInvariant()} {width}x{height}");
        }
        return string.Join(", ", sizes);
    }

    /// <summary>Takes the content out of the never-shown window, lays it out and draws it.</summary>
    private static string Render(Window window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Child = content };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        if (host.ActualWidth < 1 || host.ActualHeight < 1)
            throw new SelfTestFailure($"laid out at {host.ActualWidth:0}x{host.ActualHeight:0}");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        host.Child = null;
        return $"{bitmap.PixelWidth}x{bitmap.PixelHeight}";
    }

    private static UsageSnapshot SamplePlanUsage(DateTimeOffset now) => new()
    {
        Mode = UsageMode.PlanLimits,
        FiveHourUtilizationPercent = 43,
        FiveHourResetsAt = now.AddMinutes(125),
        WeeklyUtilizationPercent = 14,
        WeeklyResetsAt = now.AddDays(4),
        FetchedAt = now,
        SourceId = ClaudeFeedUsageSource.Id,
        Trust = SourceTrust.Official
    };

    private static UsageSnapshot SampleMonthlyUsage(DateTimeOffset now) => new()
    {
        Mode = UsageMode.MonthlySpend,
        HasMonthlySpend = true,
        MonthlySpendUsedUsd = 96.75m,
        MonthlySpendLimitUsd = 200m,
        MonthlySpendPercent = 48.4,
        FetchedAt = now
    };

    private static SessionInfo SampleSession(DateTimeOffset now) => new()
    {
        SessionId = "00000000-0000-4000-8000-000000000001",
        SessionName = "Sample session",
        TranscriptPath = @"C:\dev\sample\00000000-0000-4000-8000-000000000001.jsonl",
        WorkingDirectory = @"C:\dev\sample",
        Model = "claude-sonnet-5",
        StartedAt = now.AddMinutes(-7),
        LastActivityAt = now.AddSeconds(-30),
        InputTokens = 50,
        OutputTokens = 4_012,
        CacheReadTokens = 300_821,
        ContextWindowSize = ModelContextWindows.Lookup("claude-sonnet-5"),
        CurrentContextTokens = 47_309,
        EstimatedCostUsd = 1.25m
    };
}
