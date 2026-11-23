using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using StickyAIusage.Services;

namespace StickyAIusage.Mac;

/// <summary>
/// <c>StickyAIusage --self-test [report-path]</c> (see <see cref="SelfTest"/>): the
/// shared checks, then Avalonia's platform set up without starting the app (native
/// libraries, App.axaml and its styles), both theme variants, and every embedded
/// asset opened — the menu bar icon among them. No window is created, nothing
/// outside a temporary folder is read or written, and the single-instance lock is
/// never taken, so it runs beside the installed widget.
/// </summary>
internal static class SelfTestRunner
{
    /// <summary>The asset the menu bar item loads at start (App.CreateMenuBarIcon).</summary>
    internal const string MenuBarIcon = "avares://StickyAIusage/Assets/menubar.png";

    /// <summary>A brush every window paints with, defined once per theme variant in Theme.axaml.</summary>
    internal const string ThemedBrush = "WindowBackgroundBrush";

    public static int Run(string? reportPath)
    {
        try
        {
            using var sandbox = SelfTestSandbox.Create();
            var version = AppIdentity.Version(typeof(App).Assembly);
            var selfTest = new SelfTest($"macOS {version}");
            var output = Console.Out;
            using var watchdog = selfTest.StartWatchdog(SelfTest.TimeLimit, reportPath, output,
                code => Environment.Exit(code), sandbox.LogText);
            DiagnosticLog.WriteInfo($"Self-test of {version} in {sandbox.Root}.");

            if (!selfTest.RunCoreChecks(sandbox, typeof(App).Assembly))
                return selfTest.Finish(reportPath, output, sandbox.LogText());
            var platformReady = false;
            selfTest.Check("Avalonia platform", () =>
            {
                Program.BuildAvaloniaApp().SetupWithoutStarting();
                platformReady = Application.Current is App;
                return platformReady ? "set up, App.axaml loaded" : throw new SelfTestFailure("no App after setup");
            });
            if (platformReady)
                CheckAppResources(selfTest);

            return selfTest.Finish(reportPath, output, sandbox.LogText());
        }
        catch (Exception ex)
        {
            return SelfTest.ReportCrash(reportPath, Console.Out, ex);
        }
    }

    /// <summary>The checks that need Avalonia set up (also run by the headless UI tests).</summary>
    internal static void CheckAppResources(SelfTest selfTest)
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            selfTest.Check($"theme {variant.Key.ToString()?.ToLowerInvariant()}", () =>
                Application.Current is { } app && app.TryGetResource(ThemedBrush, variant, out var brush) && brush is not null
                    ? $"{ThemedBrush} found"
                    : throw new SelfTestFailure($"{ThemedBrush} is missing for the {variant.Key} variant"));
        }
        selfTest.Check("menu bar icon", () =>
        {
            using var stream = AssetLoader.Open(new Uri(MenuBarIcon));
            var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            return $"{MenuBarIcon} ({bitmap.PixelSize.Width}x{bitmap.PixelSize.Height})";
        });
        selfTest.Check("share card", CheckShareCards);
        selfTest.Check("assets", () =>
        {
            var assets = AssetLoader.GetAssets(new Uri("avares://StickyAIusage/Assets/"), null).ToList();
            if (assets.Count == 0)
                throw new SelfTestFailure("no assets under avares://StickyAIusage/Assets/");
            long bytes = 0;
            foreach (var asset in assets)
            {
                using var stream = AssetLoader.Open(asset);
                if (stream.Length == 0)
                    throw new SelfTestFailure($"{asset} is empty");
                bytes += stream.Length;
            }
            return $"{assets.Count} opened ({bytes:N0} bytes)";
        });
    }

    /// <summary>A plan card (wide, dark) and a session card (portrait, light), drawn
    /// outside any window and encoded as the Share menu does.</summary>
    internal static string CheckShareCards()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new Models.UsageSnapshot
        {
            Mode = Models.UsageMode.PlanLimits, FiveHourUtilizationPercent = 43, FiveHourResetsAt = now.AddMinutes(125),
            WeeklyUtilizationPercent = 14, WeeklyResetsAt = now.AddDays(4), FetchedAt = now
        };
        var session = new Models.SessionInfo
        {
            SessionId = "00000000-0000-4000-8000-000000000001", TranscriptPath = "/tmp/sample.jsonl", WorkingDirectory = "/tmp/sample",
            Model = "claude-sonnet-5", StartedAt = now.AddMinutes(-7), LastActivityAt = now, OutputTokens = 4_012,
            CacheReadTokens = 300_821, EstimatedCostUsd = 1.25m
        };
        var cards = new[]
        {
            (Sharing.ShareCards.ForUsage(usage, null, [session], null, new AppSettings(), now), true),
            ((Sharing.ShareCardModel?)Sharing.ShareCards.ForSession(session, new AppSettings { ShareCardShape = "portrait" }, now), false),
            (Sharing.ShareCards.ForYear(Sharing.YearCard.Sample(now.Year), new AppSettings()), false)
        };
        var sizes = new List<string>();
        foreach (var (card, dark) in cards)
        {
            if (card is null)
                throw new SelfTestFailure("a sample reading gave no card");
            using var bitmap = ShareCardView.Render(card, dark);
            var (width, height) = Sharing.ShareCardShapes.Size(card.Shape);
            var png = ShareCardView.Png(card, dark);
            if (bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height || png.Length < 1000)
                throw new SelfTestFailure($"{card.Variant} drew {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}, {png.Length} bytes");
            sizes.Add($"{card.Variant.ToString().ToLowerInvariant()} {width}x{height}");
        }
        return string.Join(", ", sizes);
    }
}
