using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using StickyAIusage.Mac;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

[assembly: AvaloniaTestApplication(typeof(StickyAIusage.Mac.UiTests.TestApp))]

namespace StickyAIusage.Mac.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders each macOS screen with sample data to PNG (UI_SCREENSHOT_DIR, or a
/// temp folder). Neutral sample data only - a fictional acme-webapp project.
/// </summary>
public class ScreenshotTests
{
    private static readonly string OutDir = Environment.GetEnvironmentVariable("UI_SCREENSHOT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "stickyaiusage-mac-screenshots");

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly IBrush Desktop = new SolidColorBrush(Color.Parse("#2A2E3D"));
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#D8DAE0"));

    [AvaloniaFact]
    public void Pill_states_render()
    {
        Save(Pill(Plan(), compact: true), "mac-01-pill-plan-menubar.png");
        Save(Pill(Plan(), compact: false), "mac-02-pill-plan.png");
        Save(Pill(Plan(credits: true, weekly: 86, staleFor: TimeSpan.FromMinutes(12)), compact: false), "mac-03-pill-plan-credits-stale.png");
        Save(Pill(Monthly(), compact: false, spentToday: 4.20m), "mac-04-pill-monthly.png");
        Save(Pill(Monthly(), compact: true, spentToday: 4.20m), "mac-05-pill-monthly-menubar.png");
        Save(Pill(Monthly(), compact: false, spentToday: 13.60m), "mac-22-pill-day-over-target.png");
        Save(Pill(Monthly(), compact: false, spentToday: 4.20m,
            settings: new AppSettings { ShowDayTargetAmount = false, ShowDayTargetPercent = false }), "mac-23-pill-day-ring-only.png");
        Save(Pill(Monthly(), compact: false, spentToday: 4.20m,
            settings: new AppSettings { ShowDayTargetRing = false, ShowDayTargetPercent = false }), "mac-24-pill-day-amount-only.png");
        Save(Pill(new UsageSnapshot { RequiresReauth = true, IsStale = true }, compact: false), "mac-06-pill-sign-in.png");
        Save(Pill(Plan(), compact: false, paused: true), "mac-07-pill-paused.png");
    }

    [AvaloniaFact]
    public void Menu_bar_pill_squeezes_to_fit_beside_the_notch()
    {
        var window = Pill(Plan(), compact: true);
        var widths = Enumerable.Range(0, 3).Select(window.SqueezeForMenuBar).ToList();
        Assert.True(widths[0] > widths[1] && widths[1] > widths[2], string.Join(" > ", widths));
        Save(window, "mac-14-pill-plan-menubar-tight.png");
    }

    [AvaloniaFact]
    public void Flyout_renders_for_both_plan_types()
    {
        Save(Flyout(Plan(credits: true)), "mac-08-flyout-plan.png");
        Save(Flyout(Monthly(), spentToday: 4.20m), "mac-09-flyout-monthly.png");
    }

    [AvaloniaFact]
    public void Flyout_opens_where_there_is_room_and_stays_on_screen()
    {
        // Near the bottom of the screen: opens above, arrow points down (to close).
        var low = Flyout(Plan(), show: true);
        var screen = low.Screens.Primary!.WorkingArea;
        low.PinPill(screen.Right - 300, screen.Bottom - 60);
        Assert.True(low.OpensAbove == true, $"screen={low.Screens.Primary!.Bounds} work={screen} opensAbove={low.OpensAbove} pos={low.Position} size={low.Bounds}");
        Assert.True(low.Position.Y >= screen.Y, $"pos={low.Position}");
        Save(low, "mac-12-flyout-opens-above.png");

        // Hanging off the right edge: pulled back so pill and flyout are fully visible.
        var edge = Flyout(Plan(), show: true);
        edge.PinPill(screen.Right + 250, 200);
        Assert.True(edge.OpensAbove == false, $"opensAbove={edge.OpensAbove}");
        // Window x + 12 shadow margin + 500 flyout width must end on screen.
        Assert.True(edge.Position.X + 12 + 500 <= screen.Right, $"pos={edge.Position}");
        Save(edge, "mac-13-flyout-right-edge.png");
    }

    [AvaloniaFact]
    public void Light_theme_renders()
    {
        using var light = new LightTheme();
        Save(Pill(Plan(), compact: false), "mac-15-pill-plan-light.png", background: Paper);
        Save(Flyout(Plan(credits: true)), "mac-16-flyout-plan-light.png", background: Paper);
        Save(new SettingsWindow(), "mac-17-settings-light.png", background: null);
    }

    /// <summary>Switches the app to the light theme for one test.</summary>
    private sealed class LightTheme : IDisposable
    {
        public LightTheme()
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            StickyAIusage.Mac.Controls.Palette.IsDark = false;
        }

        public void Dispose()
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            StickyAIusage.Mac.Controls.Palette.IsDark = true;
        }
    }

    [AvaloniaFact]
    public void Field_customizer_renders_and_drag_and_drop_rearranges_the_card()
    {
        var settings = new AppSettings();
        var window = new FieldsWindow(settings, () => { });
        Save(window, "mac-18-fields.png", background: null);

        // What a drag does, without a pointer: the chip lands in the row it
        // was dropped on and takes that position's color.
        var edited = new FieldsWindow(settings, () => { });
        edited.MoveField("cost", rowIndex: 0, position: 0);
        edited.MoveField("git-branch", rowIndex: 3, position: 0);
        Assert.Equal("cost,model,effort", string.Join(",", edited.Layout.Rows[0]));
        Assert.Equal("git-branch", string.Join(",", edited.Layout.Rows[3]));
        Save(edited, "mac-19-fields-rearranged.png", background: null);

        // A group stays together: picking Session ID from the catalog puts it
        // beside the other Core fields rather than at the end of the card.
        var grouped = new FieldsWindow(new AppSettings(), () => { });
        grouped.AddField("session-id");
        Assert.Equal("model,effort,session-id", string.Join(",", grouped.Layout.Rows[0]));

        // A row holds eight. A ninth chip dropped on it stays where it was,
        // and the card says why.
        var refused = new FieldsWindow(new AppSettings(), () => { });
        foreach (var field in StickyAIusage.ViewModels.SessionFields.InCategory(FieldCategory.Git).Take(8))
            refused.AddField(field.Id);
        var before = refused.Layout.Rows.Select(r => string.Join(",", r)).ToList();
        refused.MoveField("model", rowIndex: 3, position: 8);
        Assert.Equal(before, refused.Layout.Rows.Select(r => string.Join(",", r)));
        Save(refused, "mac-21-fields-row-full.png", background: null);

        // Every field on the card: the rows and the preview each keep their
        // share of the window and scroll, instead of one pushing the other out.
        var full = new FieldsWindow(new AppSettings(), () => { });
        foreach (var field in StickyAIusage.ViewModels.SessionFields.All)
            full.AddField(field.Id);
        full.Width = 1100;
        full.Height = 820;
        Save(full, "mac-20-fields-all.png", background: null);
    }

    [AvaloniaFact]
    public void Settings_renders()
    {
        var settings = new SettingsWindow();
        Save(settings, "mac-10-settings.png", background: null);

        var invalid = new SettingsWindow();
        invalid.YellowBox.Text = "90";
        Assert.False(invalid.ValidateThresholds().IsValid);
        Assert.False(invalid.SaveButton.IsEnabled);
        Save(invalid, "mac-11-settings-invalid.png", background: null);

        var actions = new SettingsActions(() => { }, () => { }, () => { }, () => { });
        var badTarget = new SettingsWindow(new AppSettings(), actions, UsageMode.MonthlySpend);
        badTarget.DayTargetBox.Text = "0";
        Assert.False(badTarget.ValidateDayTarget().IsValid);
        Assert.False(badTarget.SaveButton.IsEnabled);
        Save(badTarget, "mac-25-settings-day-target-invalid.png", background: null);

        // Pro/Max: the day target is off, with a note - and an invalid target
        // there doesn't block Save.
        var plan = new SettingsWindow(new AppSettings(), actions, UsageMode.PlanLimits);
        Assert.False(plan.DayTargetSection.IsEnabled);
        Assert.True(plan.DayTargetNote.IsVisible);
        plan.DayTargetBox.Text = "0";
        Assert.True(plan.SaveButton.IsEnabled);
        Save(plan, "mac-26-settings-plan-day-target-off.png", background: null);
    }

    private static WidgetWindow Pill(UsageSnapshot usage, bool compact, bool paused = false,
        decimal? spentToday = null, AppSettings? settings = null)
    {
        var window = new WidgetWindow(settings ?? new AppSettings(), live: false);
        window.ApplyUsage(usage, Session(), spentToday);
        window.SetCompact(compact);
        window.SetRunning(!paused);
        return window;
    }

    private static WidgetWindow Flyout(UsageSnapshot usage, bool show = false, decimal? spentToday = null)
    {
        var window = new WidgetWindow(new AppSettings(), live: false);
        window.ApplyUsage(usage, Session(), spentToday);
        if (show)
            window.Show();
        window.ToggleExpanded();
        window.ShowSessions(SampleCards());
        return window;
    }

    private static void Save(Window window, string fileName, IBrush? background = null)
    {
        Directory.CreateDirectory(OutDir);
        window.Background = background ?? (window is WidgetWindow ? Desktop : window.Background);
        if (!window.IsVisible)
            window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(OutDir, fileName));
        window.Close();
    }

    private static UsageSnapshot Plan(bool credits = false, double weekly = 14, TimeSpan? staleFor = null) => new()
    {
        Mode = UsageMode.PlanLimits,
        SubscriptionType = "pro",
        FiveHourUtilizationPercent = 43,
        FiveHourResetsAt = Now.AddMinutes(126),
        WeeklyUtilizationPercent = weekly,
        WeeklyResetsAt = Now.AddDays(5).AddHours(3),
        ExtraUsageEnabled = credits,
        CreditBalance = credits ? 9.32m : null,
        FetchedAt = Now - (staleFor ?? TimeSpan.FromSeconds(20)),
        IsStale = staleFor is not null
    };

    private static UsageSnapshot Monthly() => new()
    {
        Mode = UsageMode.MonthlySpend,
        HasMonthlySpend = true,
        MonthlySpendUsedUsd = 96.75m,
        MonthlySpendLimitUsd = 200m,
        MonthlySpendPercent = 48.4,
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
        TranscriptPath = $"/Users/me/.claude/projects/-Users-me-dev-AcmeWebApp/{id}.jsonl",
        WorkingDirectory = "/Users/me/dev/AcmeWebApp",
        Model = model,
        ThinkingEffort = "high",
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
            ran: TimeSpan.FromMinutes(103), idle: TimeSpan.FromMinutes(40)), Now)
    ];
}
