using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StickyAIusage.Demo;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Tests.Shared;
using StickyAIusage.ViewModels;
using Xunit;

namespace StickyAIusage.UiTests;

/// <summary>
/// The README's Windows pictures, drawn from the demo's data (<c>--demo</c>) through the graph the
/// app runs on, into UI_MARKETING_DIR\windows (<c>scripts/refresh-screenshots.ps1</c>) or a temporary
/// folder. Run inside <see cref="ScreenshotTests.Every_screen_renders"/> (one WPF application per
/// process). WPF draws nothing while the Windows session is locked; a marketing run (UI_MARKETING_DIR
/// set) then fails instead of handing the script blank pictures.
/// </summary>
internal static class MarketingRenders
{
    private sealed record Scene(UsageSummary Summary, List<SessionCardViewModel> Cards, DemoData Data, DateTimeOffset Now);

    private static Scene SceneAt(DemoVariant variant, double seconds)
    {
        var now = DateTimeOffset.UtcNow;
        var data = DemoData.At(variant, now, TimeSpan.FromSeconds(seconds));
        var root = Path.Combine(Path.GetTempPath(), "stickyaiusage-marketing-scenes", Guid.NewGuid().ToString("N"));
        try
        {
            using var services = AppServices.CreateDemo(new AppSettings(), data, root, () => now);
            // The feed works on the thread pool and never comes back to this thread: waiting is safe.
            var summary = services.Feed.GetSummaryAsync().GetAwaiter().GetResult();
            var cards = services.Feed.GetCardsAsync(1).GetAwaiter().GetResult();
            return new Scene(summary, cards, data, now);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    public static void Render(Brush desktop)
    {
        var folder = Path.Combine(ReadmePictures.OutputFolder, "windows");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "*.png"))
            File.Delete(old);
        var strict = Environment.GetEnvironmentVariable("UI_MARKETING_DIR") is { Length: > 0 };

        var start = SceneAt(DemoVariant.Plan, 0);
        var calm = SceneAt(DemoVariant.Plan, 40);
        var late = SceneAt(DemoVariant.Plan, 110);
        var cap = SceneAt(DemoVariant.Cap, 100);
        Assert.Equal(late.Data.FiveHourPercent(late.Now), late.Summary.Usage.FiveHourUtilizationPercent);

        void Save(FrameworkElement content, string name) => SaveScaled(content, desktop, Path.Combine(folder, name), strict);

        Save(Widget(late), "pill-plan-forecast.png");
        Save(Widget(late, expanded: true), "flyout-plan-pace.png");
        Save(Widget(cap, lights: false), "pill-orange-stale.png");
        Save(Widget(calm, paused: true), "pill-paused.png");
        Save(Widget(calm, lights: false, usage: calm.Summary.Usage with { FetchedAt = calm.Now.AddMinutes(-12) }), "pill-plan-feed-as-of.png");
        var codexNear = start.Summary.Others.Select(o => o with
        {
            Usage = o.Usage with
            {
                Windows = o.Usage.Windows.Select(w => w.Kind == UsageWindowKinds.FiveHour ? w with { PercentUsed = 91 } : w).ToList()
            }
        }).ToList();
        Save(Widget(start, expanded: true, others: codexNear), "flyout-codex.png");

        Assert.Equal(ReadmePictures.Windows.Order(), Directory.EnumerateFiles(folder, "*.png").Select(Path.GetFileName).Order());
    }

    private static FrameworkElement Widget(Scene scene, bool expanded = false, bool lights = true, bool paused = false,
        UsageSnapshot? usage = null, IReadOnlyList<ProviderReading>? others = null)
    {
        var settings = new AppSettings();
        var window = new DockedStripWindow(settings) { ShareClock = () => scene.Now };
        var summary = scene.Summary;
        window.ApplyUsage(usage ?? summary.Usage, summary.Session, summary.SpentToday, summary.Forecasts, others ?? []);
        window.ApplyActivities(lights ? summary.Activities : [], hasPlugin: true);
        window.SetRunning(!paused);
        if (expanded)
        {
            window.ToggleExpanded();
            window.ShowSessions(scene.Cards);
            // The window's own first refresh may land now: apply the scene again on top of it.
            window.ApplyUsage(usage ?? summary.Usage, summary.Session, summary.SpentToday, summary.Forecasts, others ?? []);
            window.ApplyActivities(lights ? summary.Activities : [], hasPlugin: true);
            window.ShowSessions(scene.Cards);
        }
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        if (expanded)
            content.Width = 544;
        return content;
    }

    /// <summary>At twice the size, on the desktop color; refuses a blank picture in a marketing run.</summary>
    private static void SaveScaled(FrameworkElement content, Brush background, string path, bool strict)
    {
        var host = new Border { Background = background, Padding = new Thickness(12), Child = content };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        host.Child = null;

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var colors = new HashSet<int>();
        for (var i = 0; i < pixels.Length && colors.Count < 64; i += 4)
            colors.Add(BitConverter.ToInt32(pixels, i));
        if (strict)
            Assert.True(colors.Count >= 64, $"{Path.GetFileName(path)} came out blank ({colors.Count} colors): WPF draws nothing while the " +
                                            "Windows session is locked. Unlock it and run the script again.");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
