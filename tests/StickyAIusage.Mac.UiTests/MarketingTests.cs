using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StickyAIusage.Demo;
using StickyAIusage.Mac;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.Tests.Shared;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac.UiTests;

/// <summary>
/// The README's macOS pictures, its share cards, the GitHub social preview and the demo GIF, drawn
/// from the demo's data (<c>--demo</c>) through the same graph the app runs on. Written to
/// UI_MARKETING_DIR (<c>scripts/refresh-screenshots.ps1</c> points it at a folder it then copies
/// over <c>docs/images</c>), or to a temporary folder. Every CI run draws them, so a change that
/// breaks a picture fails here; the pictures themselves change only when the script runs.
/// </summary>
public class MarketingTests
{
    /// <summary>The README pictures are drawn at twice their size, for sharp text on high-density screens.</summary>
    private const double Scale = 2;

    /// <summary>The panel at one and a half times: the headless screen is 1080 high, and a taller panel
    /// would scroll its sessions. Settings and the customizer stay at their size, for the same reason.</summary>
    private const double PanelScale = 1.5;

    private static readonly IBrush Desktop = new SolidColorBrush(Color.Parse("#2A2E3D"));
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#D8DAE0"));

    private sealed record Scene(UsageSnapshot Usage, SessionInfo? Session, decimal? SpentToday,
        IReadOnlyList<Forecasting.PaceForecast> Forecasts, IReadOnlyList<ProviderReading> Others,
        IReadOnlyList<SessionActivity> Activities, List<SessionCardViewModel> Cards, List<SessionInfo> Today, DemoData Data,
        DateTimeOffset Now);

    /// <summary>The demo <paramref name="seconds"/> into its script, read through its graph.</summary>
    private static async Task<Scene> SceneAt(DemoVariant variant, double seconds)
    {
        var now = DateTimeOffset.UtcNow;
        var data = DemoData.At(variant, now, TimeSpan.FromSeconds(seconds));
        var root = Path.Combine(Path.GetTempPath(), "stickyaiusage-marketing-scenes", Guid.NewGuid().ToString("N"));
        try
        {
            using var services = AppServices.CreateDemo(new AppSettings(), data, root, () => now);
            var summary = await services.Feed.GetSummaryAsync();
            var cards = await services.Feed.GetCardsAsync(1);
            var today = await services.Feed.GetTodaySessionsAsync();
            return new Scene(summary.Usage, summary.Session, summary.SpentToday, summary.Forecasts, summary.Others,
                summary.Activities, cards, today, data, now);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static WidgetWindow Widget(Scene scene, bool compact = false, bool expanded = false, bool lights = true,
        bool others = false, UsageSnapshot? usage = null, IReadOnlyList<ProviderReading>? otherReadings = null, bool paused = false,
        double scale = Scale)
    {
        // Scaled before anything is shown: moving the content later would reset the ring.
        var window = Scaled(new WidgetWindow(new AppSettings(), live: false) { ShareClock = () => scene.Now }, scale);
        window.ApplyUsage(usage ?? scene.Usage, scene.Session, scene.SpentToday, scene.Forecasts,
            otherReadings ?? (others ? scene.Others : []));
        window.ApplyActivities(lights ? scene.Activities : [], hasPlugin: true);
        window.SetCompact(compact);
        window.SetRunning(!paused);
        if (expanded)
        {
            window.ToggleExpanded();
            window.ShowSessions(scene.Cards);
        }
        return window;
    }

    [AvaloniaFact]
    public async Task The_readme_pictures_share_cards_social_preview_and_gif_are_drawn_from_the_demo()
    {
        var output = ReadmePictures.OutputFolder;
        var mac = Path.Combine(output, "mac");
        var share = Path.Combine(output, "share");
        foreach (var folder in new[] { mac, share })
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder, "*.png"))
                File.Delete(old);
        }

        var calm = await SceneAt(DemoVariant.Plan, 40);
        var waiting = await SceneAt(DemoVariant.Plan, 75);
        var late = await SceneAt(DemoVariant.Plan, 110);
        var cap = await SceneAt(DemoVariant.Cap, 60);
        var start = await SceneAt(DemoVariant.Plan, 0);

        // The scenes are the script's: the climb, the stages of the forecast, the lights.
        Assert.Equal(calm.Data.FiveHourPercent(calm.Now), calm.Usage.FiveHourUtilizationPercent);
        Assert.Equal(ActivityState.NeedsYou, waiting.Activities[0].State);
        Assert.Equal(3, late.Cards.Count);
        Assert.Equal(UsageMode.MonthlySpend, cap.Usage.Mode);

        Save(Widget(calm), mac, "pill-plan.png");
        Save(Widget(calm, compact: true), mac, "pill-plan-menubar.png");
        Save(Widget(waiting), mac, "pill-session-lights.png");
        Save(Widget(calm, lights: false, usage: calm.Usage with
        {
            WeeklyUtilizationPercent = 86, ExtraUsageEnabled = true, CreditBalance = 9.32m,
            FetchedAt = calm.Now.AddMinutes(-12), IsStale = true
        }), mac, "pill-plan-stale.png");
        Save(Widget(calm, paused: true), mac, "pill-paused.png");
        var signIn = Scaled(new WidgetWindow(new AppSettings(), live: false), Scale);
        signIn.ApplyUsage(new UsageSnapshot { RequiresReauth = true, IsStale = true }, calm.Session);
        Save(signIn, mac, "pill-sign-in.png");
        Save(Widget(cap), mac, "pill-monthly.png");
        Save(Widget(cap, compact: true), mac, "pill-monthly-menubar.png");
        var codexNear = start.Others.Select(o => o with
        {
            Usage = o.Usage with { Windows = o.Usage.Windows.Select(w => w.Kind == UsageWindowKinds.FiveHour ? w with { PercentUsed = 91 } : w).ToList() }
        }).ToList();
        Save(Widget(start, compact: true, lights: false, otherReadings: codexNear), mac, "pill-codex-menubar.png");

        Save(Widget(late, expanded: true, scale: PanelScale), mac, "flyout-plan.png");
        Save(Widget(waiting, expanded: true, scale: PanelScale), mac, "flyout-session-lights.png");
        Save(Widget(cap, expanded: true, scale: PanelScale), mac, "flyout-monthly.png");
        Save(Widget(late, expanded: true, others: true, scale: PanelScale), mac, "flyout-codex.png");
        Save(new SettingsWindow(), mac, "settings.png", background: null);
        Save(new FieldsWindow(new AppSettings(), () => { }), mac, "fields.png", background: null);
        using (new LightTheme())
        {
            Save(Widget(late, expanded: true, scale: PanelScale), mac, "flyout-plan-light.png", Paper);
            Save(new SettingsWindow(), mac, "settings-light.png", background: null);
        }

        var settings = new AppSettings();
        File.WriteAllBytes(Path.Combine(share, "limits-wide-dark.png"), ShareCardView.Png(
            ShareCards.ForUsage(late.Usage, late.Forecasts, late.Today, late.SpentToday, settings, late.Now)!, dark: true));
        File.WriteAllBytes(Path.Combine(share, "session-wide-light.png"), ShareCardView.Png(
            ShareCards.ForSession(late.Today[0], settings, late.Now), dark: false));
        File.WriteAllBytes(Path.Combine(share, "year-wide-dark.png"), ShareCardView.Png(
            ShareCards.ForYear(YearCard.Sample(2026), settings)!, dark: true));

        WriteSocialPreview(Path.Combine(output, "social-preview.png"), late);
        await WriteDemoGif(Path.Combine(output, "demo.gif"));

        // Exactly the pictures the READMEs show (the Core tests hold the READMEs to the same list).
        Assert.Equal(ReadmePictures.Mac.Order(), Directory.EnumerateFiles(mac, "*.png").Select(Path.GetFileName).Order());
        Assert.Equal(ReadmePictures.Share.Order(), Directory.EnumerateFiles(share, "*.png").Select(Path.GetFileName).Order());
        foreach (var top in ReadmePictures.Top)
            Assert.True(File.Exists(Path.Combine(output, top)), top);
        var pill = PngSize(File.ReadAllBytes(Path.Combine(mac, "pill-plan.png")));
        Assert.True(pill.Width >= 400, $"pill-plan.png is {pill.Width} px wide: drawn at 2x");
    }

    /// <summary>1280 × 640, as GitHub asks: the logo, the name, the tagline, a large pill near its limit.</summary>
    private static void WriteSocialPreview(string path, Scene scene)
    {
        using var pill = RenderWindow(Widget(scene, lights: true, scale: 3), Brushes.Transparent);
        var logo = new Bitmap(Path.Combine(RepositoryRoot(), "docs", "images", "logo.png"));
        var card = new Border
        {
            Width = 1280,
            Height = 640,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#151924"), 0), new GradientStop(Color.Parse("#2A2E3D"), 1) }
            },
            Padding = new Thickness(80, 64),
            Child = new StackPanel
            {
                Spacing = 28,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 28,
                        Children =
                        {
                            new Image { Source = logo, Width = 112, Height = 112 },
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Spacing = 6,
                                Children =
                                {
                                    Text("Sticky AI Usage", 68, FontWeight.Bold, "#FFFFFF"),
                                    Text("Know before you hit the limit.", 38, FontWeight.Normal, "#C9CEDB")
                                }
                            }
                        }
                    },
                    new Border
                    {
                        Margin = new Thickness(-8, 30, 0, 6),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new Image { Source = pill, Width = 1100, Height = 1100.0 * pill.PixelSize.Height / pill.PixelSize.Width }
                    },
                    Text("Windows  ·  macOS  ·  Claude Code plugin", 30, FontWeight.SemiBold, "#8FA3FF"),
                    Text("Your limits, the pace, and which session needs you. Free and open source.", 26, FontWeight.Normal, "#C9CEDB")
                }
            }
        };
        using var bitmap = RenderControl(card, 1280, 640, 1);
        bitmap.Save(path);
        Assert.Equal((1280, 640), PngSize(File.ReadAllBytes(path)));

        static TextBlock Text(string text, double size, FontWeight weight, string color) =>
            new() { Text = text, FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(Color.Parse(color)) };
    }

    /// <summary>The demo GIF's frames: 800 wide (the README shows it at 560), a caption bar on top of each.</summary>
    private const int GifWidth = 800;
    private const int GifHeight = 450;
    private const int CaptionHeight = 52;

    /// <summary>How far down the panel (drawn at <see cref="PanelScale"/>) its first session card starts, and where
    /// Claude's rows are followed by the Codex card: the GIF scrolls there.</summary>
    private const double SessionsOffset = 375;
    private const double CodexCardOffset = 200;

    /// <summary>The demo's story in one loop, each part under a caption: the pill climbs from 62% while its
    /// forecast turns yellow, orange and red and a session starts waiting; the panel's pace lines and its
    /// sessions; Codex beside Claude; a monthly cap; the share cards and the year card; the customizer; the
    /// light theme; and a closing card with where to get it.</summary>
    private static async Task WriteDemoGif(string path)
    {
        const int width = GifWidth;
        const int height = GifHeight;
        var frames = new List<GifWriter.Frame>();

        // 1. The pill through the demo's two minutes.
        foreach (var second in new[] { 0, 15, 30, 45, 55, 65, 75, 90, 105, 120 })
        {
            var scene = await SceneAt(DemoVariant.Plan, second);
            using var pill = RenderWindow(Widget(scene, scale: 2), Desktop);
            frames.Add(Compose(pill, fitWidth: 780, delay: second == 120 ? 150 : 60,
                caption: second < 75 ? "Know before you hit the limit" : "…and which session needs you"));
        }

        // 2. The panel: where each window is heading, then the sessions, the waiting one first.
        var end = await SceneAt(DemoVariant.Plan, 120);
        var waiting = await SceneAt(DemoVariant.Plan, 75);
        using (var panel = RenderWindow(Widget(end, expanded: true, scale: PanelScale), Desktop))
            frames.Add(Compose(panel, fitWidth: panel.PixelSize.Width, delay: 280, caption: "Where each window is heading", offset: 0));
        using (var panel = RenderWindow(Widget(waiting, expanded: true, scale: PanelScale), Desktop))
            frames.Add(Compose(panel, fitWidth: panel.PixelSize.Width, delay: 300, caption: "Every Claude Code session at a glance",
                offset: SessionsOffset));

        // 3. Codex beside Claude: its chip on the pill when it is the closer one, its card in the panel.
        var start = await SceneAt(DemoVariant.Plan, 0);
        var codexNear = start.Others.Select(o => o with
        {
            Usage = o.Usage with { Windows = o.Usage.Windows.Select(w => w.Kind == UsageWindowKinds.FiveHour ? w with { PercentUsed = 91 } : w).ToList() }
        }).ToList();
        using (var pill = RenderWindow(Widget(start, lights: false, otherReadings: codexNear, scale: 2), Desktop))
            frames.Add(Compose(pill, fitWidth: 780, delay: 200, caption: "Codex beside Claude"));
        using (var panel = RenderWindow(Widget(end, expanded: true, others: true, scale: PanelScale), Desktop))
            frames.Add(Compose(panel, fitWidth: panel.PixelSize.Width, delay: 260, caption: "Codex beside Claude",
                offset: CodexCardOffset));

        // 4. A monthly spend cap: the pill and the panel.
        var cap = await SceneAt(DemoVariant.Cap, 60);
        using (var pill = RenderWindow(Widget(cap, scale: 2), Desktop))
            frames.Add(Compose(pill, fitWidth: 780, delay: 180, caption: "Monthly spend caps too"));
        using (var panel = RenderWindow(Widget(cap, expanded: true, scale: PanelScale), Desktop))
            frames.Add(Compose(panel, fitWidth: panel.PixelSize.Width, delay: 240, caption: "Monthly spend caps too", offset: 0));

        // 5. Share cards: today, one session, and the year.
        var settings = new AppSettings();
        using (var card = ShareCardView.Render(ShareCards.ForUsage(end.Usage, end.Forecasts, end.Today, end.SpentToday, settings, end.Now)!,
                   dark: true))
            frames.Add(Compose(card, fitWidth: 740, delay: 280, caption: "Share your day in one click"));
        using (var card = ShareCardView.Render(ShareCards.ForSession(end.Today[0], settings, end.Now), dark: false))
            frames.Add(Compose(card, fitWidth: 740, delay: 260, caption: "…or what one session cost"));
        using (var card = ShareCardView.Render(ShareCards.ForYear(YearCard.Sample(2026), settings)!, dark: true))
            frames.Add(Compose(card, fitWidth: 740, delay: 300, caption: "Your year with Claude Code"));

        // 6. The customizer, and the light theme.
        using (var fields = RenderWindow(new FieldsWindow(new AppSettings(), () => { }), null))
            frames.Add(Compose(fields, fitWidth: 780, delay: 260, caption: "Make the card yours: drag and drop any field"));
        using (new LightTheme())
        using (var panel = RenderWindow(Widget(end, expanded: true, scale: PanelScale), Paper))
            frames.Add(Compose(panel, fitWidth: panel.PixelSize.Width, delay: 220, caption: "Light or dark", offset: 0));

        // 7. Where to get it.
        frames.Add(ClosingCard(delay: 400));

        var gif = GifWriter.Encode(width, height, frames);
        await File.WriteAllBytesAsync(path, gif);
        Assert.True(gif.Length < 5 * 1024 * 1024, $"demo.gif is {gif.Length / 1024} KB");

        // Read back with Skia's decoder: every frame, the right size, and each frame's pixels within its
        // palette's rounding of what was drawn (the light frames and the cards too).
        using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(gif));
        Assert.NotNull(codec);
        Assert.Equal(frames.Count, codec.FrameCount);
        Assert.Equal((width, height), (codec.Info.Width, codec.Info.Height));
        for (var index = 0; index < frames.Count; index++)
        {
            var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            using var decoded = new SkiaSharp.SKBitmap(info);
            Assert.Equal(SkiaSharp.SKCodecResult.Success,
                codec.GetPixels(info, decoded.GetPixels(), new SkiaSharp.SKCodecOptions(index)));
            var drawn = frames[index].Bgra;
            var far = 0;
            for (var p = 0; p < width * height; p += 97)
            {
                var color = decoded.GetPixel(p % width, p / width);
                var distance = Math.Abs(color.Blue - drawn[p * 4]) + Math.Abs(color.Green - drawn[p * 4 + 1]) + Math.Abs(color.Red - drawn[p * 4 + 2]);
                if (distance > 60)
                    far++;
            }
            Assert.True(far < width * height / 97 / 100, $"frame {index}: {far} sampled pixels far from what was drawn");
        }
    }

    private static readonly IBrush CaptionBar = new SolidColorBrush(Color.Parse("#151924"));
    private static readonly IBrush CaptionText = new SolidColorBrush(Color.Parse("#EEF1F8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#8FA3FF"));

    /// <summary>One frame: the caption in a bar on top, and under it the picture <paramref name="fitWidth"/> wide
    /// (never more than the frame, nor taller than the space under the bar unless <paramref name="offset"/>
    /// scrolls it), centered on the desktop color.</summary>
    /// <param name="offset">Show the picture from this many pixels down, cut at the frame's bottom; null: the
    /// whole picture, centered.</param>
    private static GifWriter.Frame Compose(Bitmap picture, double fitWidth, int delay, string caption, double? offset = null)
    {
        const int width = GifWidth;
        const int height = GifHeight;
        const double area = height - CaptionHeight;
        var scale = Math.Min(fitWidth, width) / picture.PixelSize.Width;
        if (offset is null)
            scale = Math.Min(scale, (area - 20.0) / picture.PixelSize.Height);
        var image = new Image { Source = picture, Width = picture.PixelSize.Width * scale, Height = picture.PixelSize.Height * scale };
        var bar = new Border
        {
            Width = width,
            Height = CaptionHeight,
            Background = CaptionBar,
            BorderBrush = Accent,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Child = new TextBlock
            {
                Text = caption,
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
                Foreground = CaptionText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        // The picture first, then the bar: a scrolled panel slides under it.
        var canvas = new Canvas { Width = width, Height = height, Background = Desktop, ClipToBounds = true, Children = { image, bar } };
        Canvas.SetLeft(image, (width - image.Width) / 2);
        Canvas.SetTop(image, offset is { } down ? CaptionHeight - down : CaptionHeight + (area - image.Height) / 2);
        return Pixels(canvas, delay);
    }

    /// <summary>The last frame: the logo, the name, what it does and where to get it.</summary>
    private static GifWriter.Frame ClosingCard(int delay)
    {
        var logo = new Bitmap(Path.Combine(RepositoryRoot(), "docs", "images", "logo.png"));
        var card = new Border
        {
            Width = GifWidth,
            Height = GifHeight,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#151924"), 0), new GradientStop(Color.Parse("#2A2E3D"), 1) }
            },
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 14,
                Children =
                {
                    new Image { Source = logo, Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center },
                    Line("Sticky AI Usage", 46, FontWeight.Bold, "#FFFFFF"),
                    Line("Know before you hit the limit.", 26, FontWeight.Normal, "#C9CEDB"),
                    Line("Windows  ·  macOS  ·  Claude Code plugin  ·  free and open source", 19, FontWeight.SemiBold, "#8FA3FF"),
                    Line("github.com/mohammad-k-shah/stickyAIusage", 21, FontWeight.SemiBold, "#EEF1F8")
                }
            }
        };
        return Pixels(card, delay);

        static TextBlock Line(string text, double size, FontWeight weight, string color) =>
            new()
            {
                Text = text, FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(Color.Parse(color)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
    }

    /// <summary>A control drawn at the GIF's size, as a frame.</summary>
    private static GifWriter.Frame Pixels(Control control, int delay)
    {
        const int width = GifWidth;
        const int height = GifHeight;
        using var bitmap = RenderControl(control, width, height, 1);
        var pixels = new byte[width * height * 4];
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, pixels.Length, width * 4);
            System.Runtime.InteropServices.Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
        return new GifWriter.Frame(pixels, delay);
    }

    private static RenderTargetBitmap RenderControl(Control control, int width, int height, double scale)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(width * scale), (int)(height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        return bitmap;
    }

    /// <summary>The window's content laid out <paramref name="scale"/> times larger inside its own window
    /// (its styles and resources stay), so the picture is drawn large rather than resampled.</summary>
    private static T Scaled<T>(T window, double scale) where T : Window
    {
        if (scale == 1)
            return window;
        var content = (Control)window.Content!;
        window.Content = null;
        window.Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };
        return window;
    }

    /// <summary>A window as it renders (taken as the screenshot tests take it); closed after.</summary>
    private static RenderTargetBitmap RenderWindow(Window window, IBrush? background)
    {
        if (background is not null)
            window.Background = background;
        window.Show();
        // The first frame lays the window out at its size; the second shows it.
        window.CaptureRenderedFrame()?.Dispose();
        var frame = window.CaptureRenderedFrame()!;
        var bitmap = new RenderTargetBitmap(frame.PixelSize, new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
            context.DrawImage(frame, new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height));
        frame.Dispose();
        window.Close();
        return bitmap;
    }

    private static void Save(Window window, string folder, string fileName, IBrush? background = null)
    {
        var brush = window is WidgetWindow ? background ?? Desktop : background;
        using var bitmap = RenderWindow(window, brush);
        bitmap.Save(Path.Combine(folder, fileName));
    }

    private static (int Width, int Height) PngSize(byte[] png) =>
        ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "StickyAIusage.sln")))
                return folder.FullName;
        throw new DirectoryNotFoundException("The repository root (StickyAIusage.sln) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Switches the app to the light theme for a few pictures.</summary>
    private sealed class LightTheme : IDisposable
    {
        public LightTheme()
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Controls.Palette.IsDark = false;
        }

        public void Dispose()
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Controls.Palette.IsDark = true;
        }
    }
}
