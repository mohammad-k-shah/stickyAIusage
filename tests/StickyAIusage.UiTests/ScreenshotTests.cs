using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.UiTests;

/// <summary>
/// Renders every screen with sample data, off-screen, to PNG. This catches
/// XAML problems that only surface at runtime (a missing resource, a broken
/// binding in a template, a bad trigger), and the images are uploaded by CI so
/// the design can be reviewed without running the app. Everything runs in one
/// test on one STA thread: WPF objects belong to the thread that created them,
/// and an Application can only be created once per process.
/// </summary>
public class ScreenshotTests
{
    private static readonly string OutDir = Environment.GetEnvironmentVariable("UI_SCREENSHOT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "stickyaiusage-screenshots");

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Every_screen_renders()
    {
        RunOnSta(() =>
        {
            Directory.CreateDirectory(OutDir);
            var app = new App();
            app.InitializeComponent();

            var desktop = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x3D));
            var settings = new AppSettings();

            // Collapsed pill states.
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), spentToday: 4.20m), desktop, "01-pill.png");
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), spentToday: 13.60m), desktop, "18-pill-day-over-target.png");
            Save(Pill(new AppSettings { ShowDayTargetAmount = false, ShowDayTargetPercent = false }, Usage(96.75m, 48.4), Session(),
                spentToday: 4.20m), desktop, "19-pill-day-ring-only.png");
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), spentToday: 4.20m, compact: true), desktop, "20-pill-taskbar.png");
            Save(Pill(settings, Usage(176.10m, 88.1, staleFor: TimeSpan.FromMinutes(12)), Session()), desktop, "02-pill-orange-stale.png");
            Save(Pill(settings, new UsageSnapshot { HasMonthlySpend = false, IsStale = true, RequiresReauth = true }, Session()),
                desktop, "03-pill-sign-in.png");
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), paused: true), desktop, "04-pill-paused.png");

            // Pro / Max plan layout: rolling windows and extra-usage credits.
            Save(Pill(settings, PlanUsage(), Session()), desktop, "08-pill-plan.png");
            Save(Pill(settings, PlanUsage(), Session(), compact: true), desktop, "10-pill-plan-taskbar.png");

            var planWindow = new DockedStripWindow(settings);
            planWindow.ApplyUsage(PlanUsage(), Session());
            planWindow.ToggleExpanded();
            planWindow.ShowSessions(SampleCards().Take(1).ToList());
            Save(Detach(planWindow), desktop, "09-flyout-plan.png");

            // Expanded flyout with three sessions.
            var window = new DockedStripWindow(settings);
            window.ApplyUsage(Usage(96.75m, 48.4), Session(), 4.20m);
            window.ToggleExpanded();
            window.ShowSessions(SampleCards());
            Save(Detach(window), desktop, "05-flyout.png");

            // Session card field customizer, and what a drag does to a card.
            var fields = new FieldsWindow(settings, () => { });
            var fieldsContent = Detach(fields);
            fieldsContent.Width = 800;
            Save(fieldsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "14-fields.png");

            var rearranged = new FieldsWindow(settings, () => { });
            rearranged.MoveField("cost", rowIndex: 0, position: 0);
            rearranged.MoveField("git-branch", rowIndex: 3, position: 0);
            Assert.Equal("cost,model,effort", string.Join(",", rearranged.Layout.Rows[0]));
            Assert.Equal("git-branch", string.Join(",", rearranged.Layout.Rows[3]));
            var rearrangedContent = Detach(rearranged);
            rearrangedContent.Width = 800;
            Save(rearrangedContent, (Brush)app.FindResource("WindowBackgroundBrush"), "15-fields-rearranged.png");

            // A group stays together: picking Session ID from the catalog puts
            // it beside the other Core fields rather than at the end.
            var grouped = new FieldsWindow(settings, () => { });
            grouped.AddField("session-id");
            Assert.Equal("model,effort,session-id", string.Join(",", grouped.Layout.Rows[0]));

            // A row holds eight. A ninth chip dropped on it stays where it
            // was, and the card says why.
            var refused = new FieldsWindow(settings, () => { });
            foreach (var field in SessionFields.InCategory(FieldCategory.Git).Take(8))
                refused.AddField(field.Id);
            var before = refused.Layout.Rows.Select(r => string.Join(",", r)).ToList();
            refused.MoveField("model", rowIndex: 3, position: 8);
            Assert.Equal(before, refused.Layout.Rows.Select(r => string.Join(",", r)));
            var refusedContent = Detach(refused);
            refusedContent.Width = 1100;
            refusedContent.Height = 820;
            Save(refusedContent, (Brush)app.FindResource("WindowBackgroundBrush"), "17-fields-row-full.png");

            // Every field on the card: the rows and the preview each keep
            // their share of the window and scroll, rather than overlapping.
            var allFields = new FieldsWindow(settings, () => { });
            foreach (var field in SessionFields.All)
                allFields.AddField(field.Id);
            var allFieldsContent = Detach(allFields);
            allFieldsContent.Width = 1100;
            allFieldsContent.Height = 820;
            Save(allFieldsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "16-fields-all.png");

            // Light theme: the same screens with the light palette merged in.
            ThemeService.ApplyForTests(dark: false);
            var paper = new SolidColorBrush(Color.FromRgb(0xD8, 0xDA, 0xE0));
            Save(Pill(settings, PlanUsage(), Session()), paper, "11-pill-plan-light.png");

            var lightFlyout = new DockedStripWindow(settings);
            lightFlyout.ApplyUsage(PlanUsage(), Session());
            lightFlyout.ToggleExpanded();
            lightFlyout.ShowSessions(SampleCards().Take(1).ToList());
            Save(Detach(lightFlyout), paper, "12-flyout-plan-light.png");

            var lightSettings = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            var lightSettingsContent = Detach(lightSettings);
            lightSettingsContent.Width = 484;
            Save(lightSettingsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "13-settings-light.png");
            ThemeService.ApplyForTests(dark: true);

            // Settings, valid and with an invalid threshold.
            var settingsWindow = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            var settingsContent = Detach(settingsWindow);
            settingsContent.Width = 484;
            Save(settingsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "06-settings.png");

            var invalid = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            invalid.YellowBox.Text = "90";
            Assert.False(invalid.ValidateThresholds().IsValid);
            Assert.False(invalid.SaveButton.IsEnabled);
            var invalidContent = Detach(invalid);
            invalidContent.Width = 484;
            Save(invalidContent, (Brush)app.FindResource("WindowBackgroundBrush"), "07-settings-invalid.png");

            var actions = new SettingsActions(() => { }, () => { }, () => { }, () => { });
            var badTarget = new SettingsWindow(settings, actions, UsageMode.MonthlySpend);
            badTarget.DayTargetBox.Text = "0";
            Assert.False(badTarget.ValidateDayTarget().IsValid);
            Assert.False(badTarget.SaveButton.IsEnabled);
            var badTargetContent = Detach(badTarget);
            badTargetContent.Width = 484;
            Save(badTargetContent, (Brush)app.FindResource("WindowBackgroundBrush"), "21-settings-day-target-invalid.png");

            // Pro/Max: the day target is off, with a note - and an invalid
            // target there doesn't block Save.
            var plan = new SettingsWindow(settings, actions, UsageMode.PlanLimits);
            Assert.False(plan.DayTargetSection.IsEnabled);
            Assert.Equal(Visibility.Visible, plan.DayTargetNote.Visibility);
            plan.DayTargetBox.Text = "0";
            Assert.True(plan.SaveButton.IsEnabled);
            var planContent = Detach(plan);
            planContent.Width = 484;
            Save(planContent, (Brush)app.FindResource("WindowBackgroundBrush"), "22-settings-plan-day-target-off.png");
        });

        Assert.True(File.Exists(Path.Combine(OutDir, "05-flyout.png")));
    }

    private static FrameworkElement Pill(AppSettings settings, UsageSnapshot usage, SessionInfo session,
        bool paused = false, bool compact = false, decimal? spentToday = null)
    {
        var window = new DockedStripWindow(settings);
        window.ApplyUsage(usage, session, spentToday);
        window.SetCompact(compact);
        window.SetRunning(!paused);
        return Detach(window);
    }

    /// <summary>Takes a window's content out of the (never shown) window so it
    /// can be measured and rendered on its own.</summary>
    private static FrameworkElement Detach(Window window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        return content;
    }

    private static void Save(FrameworkElement content, Brush background, string fileName)
    {
        var host = new Border { Background = background, Padding = new Thickness(16), Child = content };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();

        const double scale = 2; // crisp at typical 150-200% review zoom
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        host.Child = null;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(OutDir, fileName));
        encoder.Save(file);
    }

    private static UsageSnapshot Usage(decimal used, double percent, TimeSpan? staleFor = null) => new()
    {
        HasMonthlySpend = true,
        MonthlySpendUsedUsd = used,
        MonthlySpendLimitUsd = 200m,
        MonthlySpendPercent = percent,
        FetchedAt = Now - (staleFor ?? TimeSpan.FromSeconds(20)),
        IsStale = staleFor is not null
    };

    private static UsageSnapshot PlanUsage() => new()
    {
        Mode = UsageMode.PlanLimits,
        SubscriptionType = "pro",
        FiveHourUtilizationPercent = 12,
        FiveHourResetsAt = Now.AddMinutes(197),
        WeeklyUtilizationPercent = 30,
        WeeklyResetsAt = Now.AddDays(4).AddHours(3),
        ExtraUsageEnabled = true,
        ExtraUsageLimit = 50m,
        ExtraUsageUsed = 11.06m,
        FetchedAt = Now.AddSeconds(-20)
    };

    private static SessionInfo Session(
        string id = "3c8e62c4-17a7-453e-8385-88147c5aaf07", string? name = "Refactor payment retries (fork)",
        string model = "claude-sonnet-5", decimal cost = 3.05m, long input = 50, long output = 4_012,
        long cacheRead = 3_300_821, long cacheWrite = 469_573, long context = 247_309,
        TimeSpan? ran = null, TimeSpan? idle = null, GitInfo? git = null, IReadOnlyList<string>? skills = null) => new()
    {
        SessionId = id,
        SessionName = name,
        TranscriptPath = $@"C:\Users\me\.claude\projects\C--dev-acme-webapp\{id}.jsonl",
        WorkingDirectory = @"C:\dev\acme-webapp",
        Model = model,
        ThinkingEffort = "medium",
        ClaudeCodeVersion = "2.1.273",
        StartedAt = Now - (ran ?? TimeSpan.FromMinutes(7)) - (idle ?? TimeSpan.FromSeconds(30)),
        LastActivityAt = Now - (idle ?? TimeSpan.FromSeconds(30)),
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheWrite,
        ContextWindowSize = ModelContextWindows.Lookup(model),
        CurrentContextTokens = context,
        EstimatedCostUsd = cost,
        Git = git,
        SkillsUsed = skills ?? Array.Empty<string>()
    };

    private static List<SessionCardViewModel> SampleCards() =>
    [
        SessionCardViewModel.From(Session(git: new GitInfo
        {
            Branch = "feature/checkout-redesign", StagedCount = 2, UnstagedCount = 1, UntrackedCount = 4,
            Insertions = 274, Deletions = 39, AheadCount = 1, OriginOwner = "acme", OriginRepo = "acme-webapp",
            PullRequestStatus = "OPEN", CiStatus = "SUCCESS"
        }, skills: ["anthropic-skills:docx"]), Now),
        SessionCardViewModel.From(Session(id: "7481c4b9-6458-421f-b183-b35bb06d1e5d", name: "Write API integration tests",
            cost: 0.48m, input: 8, output: 2_990, cacheRead: 252_393, cacheWrite: 96_142, context: 106_495,
            ran: TimeSpan.FromMinutes(103), idle: TimeSpan.FromMinutes(40)), Now),
        SessionCardViewModel.From(Session(id: "6eba8773-0000-4000-8000-000000000000", name: null,
            model: "claude-haiku-4-5-20251001", cost: 0.06m, input: 20, output: 8_958, cacheRead: 863_287,
            cacheWrite: 55_748, context: 176_000, ran: TimeSpan.FromMinutes(4), idle: TimeSpan.FromHours(3),
            git: new GitInfo { Branch = "main" }), Now)
    ];

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
