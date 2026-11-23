using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using StickyAIusage.Forecasting;
using StickyAIusage.Health;
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
        // Claude Code's feed: an older reading reads "as of", and a gateway
        // spend limit known only as a percentage shows no dollars.
        Save(Pill(FeedPlan(TimeSpan.FromMinutes(12)), compact: false), "mac-27-pill-plan-feed-as-of.png");
        Save(Pill(FeedSpendLimit(), compact: false), "mac-28-pill-spend-limit-percent.png");
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
    public void Flyout_notice_gives_the_same_help_as_on_Windows()
    {
        // Sign in again: the message and a button that copies the command
        // (the parity gap with the Windows flyout's Sign in button).
        var signIn = Flyout(Plan() with { RequiresReauth = true, IsStale = true, SourceId = ClaudeOAuthUsageSource.Id });
        Assert.True(signIn.NoticeBar.IsVisible);
        Assert.Equal(FlyoutNotice.SignInMessage, signIn.NoticeText.Text);
        Assert.Equal("Copy command", signIn.CopyNoticeButton.Content);
        Assert.False(signIn.DismissNoticeButton.IsVisible);
        Assert.False(signIn.ConnectNoticeButton.IsVisible);
        Save(signIn, "mac-29-flyout-sign-in.png");

        var waiting = Flyout(new UsageSnapshot
        {
            IsStale = true, FetchedAt = Now, SourceId = ClaudeFeedUsageSource.Id, StatusDetail = ClaudeFeedUsageSource.WaitingDetail
        });
        Assert.Equal("Copy install command", waiting.CopyNoticeButton.Content);
        Assert.Equal((true, FlyoutNotice.ConnectLabel), (waiting.ConnectNoticeButton.IsVisible, waiting.ConnectNoticeButton.Content));
        Assert.Contains("secondary", waiting.CopyNoticeButton.Classes);
        var noticeConnects = 0;
        waiting.ConnectRequested += (_, _) => noticeConnects++;
        waiting.ConnectNoticeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, noticeConnects);
        Save(waiting, "mac-30-flyout-install-plugin.png");

        var settings = new AppSettings();
        var tip = new WidgetWindow(settings, live: false);
        tip.ApplyUsage(Plan() with { SourceId = ClaudeOAuthUsageSource.Id }, Session());
        tip.ToggleExpanded();
        Assert.True(tip.DismissNoticeButton.IsVisible);
        tip.DismissNoticeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(settings.PluginTipDismissed);
        Assert.False(tip.NoticeBar.IsVisible);
        Assert.Equal("", tip.NoticeText.Text);
        tip.Close();
    }

    [AvaloniaFact]
    public void Flyout_says_why_the_numbers_are_missing_and_what_to_do()
    {
        // No reading and no notice: the usage card says why, with one button.
        var failed = Flyout(new UsageSnapshot { IsStale = true, FetchedAt = Now });
        Assert.False(failed.NoticeBar.IsVisible);
        Assert.True(failed.SummaryCard.IsVisible);
        Assert.True(failed.HealthPanel.IsVisible);
        Assert.Equal(HealthLine.FailedDetail, failed.HealthText.Text);
        Assert.Equal("Open Health", failed.HealthButton.Content);
        Assert.True(failed.HealthButton.IsVisible);
        var healthRequests = 0;
        failed.HealthRequested += (_, _) => healthRequests++;
        failed.HealthButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, healthRequests);
        Save(failed, "mac-39-flyout-health-failed.png");

        var off = Flyout(new UsageSnapshot { IsStale = true, FetchedAt = Now, StatusDetail = AppServices.UsageOffDetail });
        Assert.Equal("Open Settings", off.HealthButton.Content);
        off.Close();

        // A current reading has no health line.
        var current = Flyout(Plan());
        Assert.False(current.HealthPanel.IsVisible);
        current.Close();
    }

    [AvaloniaFact]
    public void Settings_name_the_build_that_is_running()
    {
        var window = new SettingsWindow(new AppSettings { ClaudeSource = "auto" },
            new SettingsActions(() => { }, () => { }, () => { }, () => { }));

        Assert.StartsWith("Sticky AI Usage ", window.BuildInfoText.Text);
        Assert.Contains(" · " + AppIdentity.Profile(AppPaths.Current) + " · ", window.BuildInfoText.Text); // the tests' own data folder
        Assert.EndsWith("usage source: Automatic", window.BuildInfoText.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Session_cards_say_where_their_cost_comes_from()
    {
        var exact = Session(cost: 3.05m);
        exact.ExactCostUsd = 3.41m;
        var rough = Session(id: "9e1f0a3b-55c2-4d0e-b7a1-2c6d8e4f9a10", name: "Try the new model", model: "claude-nova-1",
            cost: 1.12m, rough: true);
        var cards = new List<SessionCardViewModel>
        {
            SessionCardViewModel.From(exact, Now),
            SampleCards()[1],
            SessionCardViewModel.From(rough, Now)
        };
        var window = new WidgetWindow(new AppSettings(), live: false);
        window.ApplyUsage(Plan(), exact);
        window.ToggleExpanded();
        window.ShowSessions(cards);
        window.Show();
        window.CaptureRenderedFrame(); // lays the cards out

        var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>()
            .Select(t => t.Text).ToList();
        Assert.Contains("$3.41", texts);
        Assert.Contains("cost", texts);
        Assert.Contains("≈$1.12", texts);
        Assert.Contains("3 sessions · $5.01 estimated", texts);
        Save(window, "mac-32-flyout-session-costs.png");
    }

    [AvaloniaFact]
    public void Pace_forecast_renders_in_the_pill_and_the_flyout()
    {
        // The limit time replaces the countdown, red within 30 minutes.
        Save(Pill(Plan(fiveHour: 88), compact: false, forecasts: PlanForecasts()), "mac-33-pill-plan-forecast.png");
        var today = Pill(Plan(fiveHour: 88), compact: true);
        var forecast = Pill(Plan(fiveHour: 88), compact: true, forecasts: PlanForecasts());
        var widthToday = today.SqueezeForMenuBar(0);
        // The chip's width follows the clock ("→ 12:58" is a digit wider than "→ 1:11"), so
        // both ends are measured at fixed times rather than at whatever time the test runs:
        // the widest may not grow the pill by more than 12 px, the narrowest is one digit less.
        var widest = Pill(Plan(fiveHour: 88), compact: true, forecasts: PlanForecasts(LocalTimeToday(12, 58))).SqueezeForMenuBar(0);
        var narrowest = Pill(Plan(fiveHour: 88), compact: true, forecasts: PlanForecasts(LocalTimeToday(1, 11))).SqueezeForMenuBar(0);
        Assert.InRange(widest - widthToday, -12, 12);
        Assert.InRange(narrowest - widthToday, -18, 12);
        Assert.Equal("→ " + Format.ShortTimeNoDesignator(Now.AddMinutes(25)), forecast.ForecastText.Text);
        Assert.True(forecast.WeeklyText.IsVisible);
        // Squeezed beside the notch, the chip stays and the weekly text goes first.
        forecast.SqueezeForMenuBar(1);
        Assert.True(forecast.ForecastText.IsVisible);
        Assert.False(forecast.WeeklyText.IsVisible);
        Save(forecast, "mac-34-pill-plan-forecast-menubar-squeezed.png");
        today.Close();
        Save(Pill(Monthly(), compact: false, spentToday: 4.20m, forecasts: [MonthForecast()]), "mac-35-pill-monthly-projection.png");

        var window = new WidgetWindow(new AppSettings(), live: false);
        window.ApplyUsage(Plan(fiveHour: 88), Session(), forecasts: PlanForecasts());
        window.ToggleExpanded();
        window.ShowSessions(SampleCards().Take(1).ToList());
        window.Show();
        window.CaptureRenderedFrame(); // lays the rows out
        var texts = VisibleTexts(window);
        Assert.Contains(texts, t => t.StartsWith("At this pace: limit at "));
        Assert.Contains(texts, t => t.Contains("points ahead of an even pace"));
        Save(window, "mac-36-flyout-plan-pace.png");

        var month = new WidgetWindow(new AppSettings(), live: false);
        month.ApplyUsage(Monthly(), Session(), 4.20m, [MonthForecast()]);
        month.ToggleExpanded();
        month.ShowSessions(SampleCards().Take(1).ToList());
        month.Show();
        month.CaptureRenderedFrame();
        Assert.Contains(VisibleTexts(month), t => t.Contains("(cap $200.00)"));
        Save(month, "mac-37-flyout-monthly-pace.png");

        // Without a forecast the rows have no second line.
        var plain = new WidgetWindow(new AppSettings(), live: false);
        plain.ApplyUsage(Plan(), Session());
        plain.ToggleExpanded();
        plain.Show();
        plain.CaptureRenderedFrame();
        Assert.DoesNotContain(VisibleTexts(plain), t => t.StartsWith("At this pace") || t.StartsWith("On pace"));
        plain.Close();

        StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark: false);
        try
        {
            Save(Pill(Plan(fiveHour: 88), compact: false, forecasts: PlanForecasts()), "mac-38-pill-plan-forecast-light.png", background: Paper);
        }
        finally
        {
            StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark: true);
        }
    }

    /// <summary>Three sessions of acme-webapp: one waiting for permission, one working, one done.</summary>
    private static IReadOnlyList<SessionActivity> Activities() => SessionActivities.From(
    [
        Record("3c8e62c4-17a7-453e-8385-88147c5aaf07", "needs_you", TimeSpan.FromMinutes(2), new FeedAttention("permission", "Bash")),
        Record("7481c4b9-6458-421f-b183-b35bb06d1e5d", "working", TimeSpan.FromMinutes(6)),
        Record("9e1f0a3b-55c2-4d0e-b7a1-2c6d8e4f9a10", "done", TimeSpan.FromMinutes(3)),
    ], Now);

    private static FeedSession Record(string id, string state, TimeSpan since, FeedAttention? attention = null) =>
        new(id, "terminal", "/Users/me/dev/AcmeWebApp", "claude-sonnet-5", "2.1.325", "2.2.0", Now, Now - since - since, Now,
            null, null, null, null, null, state, Now - since, attention, TimeSpan.FromMinutes(12));

    [AvaloniaFact]
    public void Session_lights_render_in_the_pill_and_the_cards()
    {
        var activities = Activities();
        var full = Pill(Plan(), compact: false);
        full.ApplyActivities(activities, hasPlugin: true);
        Assert.True(full.LightsGroup.IsVisible);
        Assert.Equal([LightKind.NeedsYou, LightKind.Working, LightKind.Done],
            full.LightsDots.Children.Select(c => (LightKind)c.Tag!));
        Assert.Contains("waiting for permission to use Bash", ToolTip.GetTip(full.LightsGroup) as string);
        Save(full, "mac-41-pill-session-lights.png");

        var compact = Pill(Plan(), compact: true);
        compact.ApplyActivities(activities, hasPlugin: true);
        Assert.Equal([LightKind.NeedsYou], compact.LightsDots.Children.Select(c => (LightKind)c.Tag!));
        Save(compact, "mac-42-pill-session-lights-compact.png");

        // The cards: the waiting one first, each with its status instead of "Live".
        var byId = activities.ToDictionary(a => a.SessionId);
        var cards = SessionCardViewModel.NeedsYouFirst(
        [
            SessionCardViewModel.From(Session(id: "7481c4b9-6458-421f-b183-b35bb06d1e5d", name: "Write API integration tests"), Now,
                null, byId["7481c4b9-6458-421f-b183-b35bb06d1e5d"]),
            SessionCardViewModel.From(Session(git: new GitInfo { Branch = "feature/checkout-redesign", StagedCount = 2 }), Now,
                null, byId["3c8e62c4-17a7-453e-8385-88147c5aaf07"]),
            SessionCardViewModel.From(Session(id: "9e1f0a3b-55c2-4d0e-b7a1-2c6d8e4f9a10", name: "Try the new model"), Now,
                null, byId["9e1f0a3b-55c2-4d0e-b7a1-2c6d8e4f9a10"]),
        ]);
        var window = new WidgetWindow(new AppSettings(), live: false);
        window.ApplyUsage(Plan(), Session());
        window.ApplyActivities(activities, hasPlugin: true);
        window.ToggleExpanded();
        window.ShowSessions(cards);
        window.Show();
        window.CaptureRenderedFrame();
        var texts = VisibleTexts(window);
        Assert.Contains("Needs you: permission", texts);
        Assert.Contains("Working", texts);
        Assert.Contains("Done 3m ago", texts);
        Assert.DoesNotContain("Live", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("feature/checkout"));
        Assert.False(window.LightsHintText.IsVisible);
        Assert.False(window.LightsConnectButton.IsVisible);

        // Details open with the toggle and stay open across a refresh.
        var toggle = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>()
            .First(b => b.Tag as string == "details" && b.DataContext == cards[0]);
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.CaptureRenderedFrame();
        Assert.Contains("feature/checkout-redesign", VisibleTexts(window));
        var again = cards.Select(c => SessionCardViewModel.From(Session(id: c.SessionId), Now)).ToList();
        window.ShowSessions(again);
        Assert.True(again[0].IsExpanded);
        Assert.False(again[1].IsExpanded);
        window.ShowSessions(cards);
        Save(window, "mac-43-flyout-session-lights.png");

        using (new LightTheme())
        {
            var light = Pill(Plan(), compact: false);
            light.ApplyActivities(activities, hasPlugin: true);
            Save(light, "mac-44-pill-session-lights-light.png", background: Paper);
        }

        // Without the plugin: no dots, one line in the flyout.
        var none = new WidgetWindow(new AppSettings { ClaudeSource = "auto" }, live: false);
        none.ApplyUsage(Plan(), Session());
        none.ApplyActivities([], hasPlugin: false);
        Assert.False(none.LightsGroup.IsVisible);
        none.ToggleExpanded();
        none.Show();
        none.CaptureRenderedFrame();
        Assert.True(none.LightsHintText.IsVisible);
        Assert.Equal(SessionLights.ConnectHint, none.LightsHintText.Text);
        Assert.Equal((true, SessionLights.ConnectAction), (none.LightsConnectButton.IsVisible, none.LightsConnectButton.Content));
        var connectRequests = 0;
        none.ConnectRequested += (_, _) => connectRequests++;
        none.LightsConnectButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, connectRequests);
        none.Close();

        // Switched off: nothing.
        var off = Pill(Plan(), compact: false, settings: new AppSettings { ShowSessionLights = false });
        off.ApplyActivities(activities, hasPlugin: true);
        Assert.False(off.LightsGroup.IsVisible);
        off.Close();
    }

    [AvaloniaFact]
    public void Session_lights_and_their_alerts_have_switches_that_save()
    {
        var settings = new AppSettings();
        var window = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }, () => { }));
        Assert.True(window.ShowSessionLightsToggle.IsChecked);
        Assert.True(window.AlertOnNeedsYouToggle.IsChecked);
        Assert.False(window.AlertOnDoneToggle.IsChecked);
        window.ShowSessionLightsToggle.IsChecked = false;
        window.AlertOnDoneToggle.IsChecked = true;
        window.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(settings.ShowSessionLights);
        Assert.True(settings.AlertOnDone);
        Assert.True(settings.AlertOnNeedsYou);
    }

    private static List<string> VisibleTexts(Window window) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void Share_cards_render_in_every_shape_and_theme_with_nothing_private()
    {
        Directory.CreateDirectory(OutDir);
        var sessions = new[] { Session(), Session(id: "7481c4b9-6458-421f-b183-b35bb06d1e5d", name: "Write API integration tests", cost: 0.48m) };
        foreach (var shape in new[] { "wide", "portrait" })
        {
            var shaped = new AppSettings { ShareCardShape = shape };
            var cards = new (string Name, Sharing.ShareCardModel Card)[]
            {
                ("limits", Sharing.ShareCards.ForUsage(Plan(fiveHour: 88), PlanForecasts(), sessions, null, shaped, Now)!),
                ("spend", Sharing.ShareCards.ForUsage(Monthly(), [MonthForecast()], sessions, 4.20m, shaped, Now)!),
                ("session", Sharing.ShareCards.ForSession(Session(), shaped, Now)),
                ("year", Sharing.ShareCards.ForYear(Sharing.YearCard.Sample(2026), shaped)!)
            };
            foreach (var (name, card) in cards)
            {
                var (width, height) = Sharing.ShareCardShapes.Size(card.Shape);
                foreach (var dark in new[] { true, false })
                {
                    // Drawn outside any window, exactly as the Share menu does it.
                    var png = ShareCardView.Png(card, dark);
                    Assert.Equal((width, height), PngSize(png));
                    File.WriteAllBytes(Path.Combine(OutDir, $"mac-45-share-{name}-{shape}-{(dark ? "dark" : "light")}.png"), png);
                }
                var view = new ShareCardView();
                view.Show(card, Sharing.ShareCardPalette.Light);
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();
                Assert.Contains(Sharing.ShareCards.ShortRepositoryUrl, texts);
                Assert.DoesNotContain(texts, t => t.Contains("acme", StringComparison.OrdinalIgnoreCase)
                                                 || t.Contains("Refactor payment") || t.Contains("checkout-redesign"));
                Assert.All(texts, t => Assert.Contains(t, card.AllText()));
                Assert.True(view.FooterUrlText.FontSize >= 22); // still 11 px at half size
                // Nothing reaches past the card's edges (the wide session card has five figures).
                Assert.All(view.GetVisualDescendants().OfType<Control>().Where(c => c.IsVisible),
                    c => Assert.True(c.TranslatePoint(new Point(0, c.Bounds.Height), view) is not { } bottom || bottom.Y <= height + 0.5,
                        $"{c.GetType().Name} {c.Name} ends at {c.TranslatePoint(new Point(0, c.Bounds.Height), view)} in {name} {shape}"));
            }
        }
    }

    [AvaloniaFact]
    public async Task Share_runs_from_the_flyout_and_its_settings_save()
    {
        // No reading: today's Share is off; a reading turns it on.
        var none = Flyout(new UsageSnapshot { IsStale = true, FetchedAt = Now });
        Assert.False(none.ShareButton.IsEnabled);
        Assert.Equal(Sharing.ShareFlow.NoReadingTooltip, ToolTip.GetTip(none.ShareButton));
        none.Close();

        var window = Flyout(Plan(), show: true);
        Assert.True(window.ShareButton.IsEnabled);
        window.CaptureRenderedFrame(); // lays the cards out
        Assert.Contains(window.SessionsList.GetVisualDescendants().OfType<Button>(), b => (string?)b.Tag == "share" && b.IsVisible);
        // A card's Share and the header's Share open the same menu, never a second one beside it.
        var shareLink = window.SessionsList.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Tag == "share");
        shareLink.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["Copy image", "Save image…", "Post on X", "Post on LinkedIn", "Post on Bluesky"], window.ShareMenuItems);
        window.ShareButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(5, window.ShareMenuItems.Count);
        Assert.All(window.ShareMenuItemsEnabled, Assert.True);
        var output = new FakeShareOutput();
        window.ShareOutput = output;
        await window.ShareAsync(Sharing.ShareAction.PostBluesky,
            () => Task.FromResult<Sharing.ShareCardModel?>(Sharing.ShareCards.ForSession(Session(), new AppSettings(), Now)));
        Assert.Equal("Image copied. Paste it into the Bluesky post that just opened.", window.ShareStatusText.Text);
        Assert.True(window.ShareStatusText.IsVisible);
        Assert.Equal((1200, 630), PngSize(Assert.Single(output.Copied)));
        Assert.StartsWith("https://bsky.app/intent/compose?text=", Assert.Single(output.Opened));
        await window.ShareAsync(Sharing.ShareAction.Copy, window.TodayCardAsync);
        Assert.Equal(2, output.Copied.Count);
        Save(window, "mac-46-flyout-share.png");
        Assert.Empty(window.ShareYearMenuItems); // November: no year card

        // December and January: the header's menu adds the year card (no reading needed), a
        // card's menu doesn't; the line under the header says when the year has nothing.
        var season = Flyout(new UsageSnapshot { IsStale = true, FetchedAt = Now }, shareClock: () => December);
        season.YearStats = SampleYearStats();
        Assert.True(season.ShareButton.IsEnabled);
        Assert.Equal(Sharing.ShareFlow.ShareYearTooltip, ToolTip.GetTip(season.ShareButton));
        season.ShareButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["Copy image", "Save image…", "Post on X", "Post on LinkedIn", "Post on Bluesky", "My 2026 with Claude Code"],
            season.ShareMenuItems);
        Assert.Equal(["Copy image", "Save image…", "Post on X", "Post on LinkedIn", "Post on Bluesky"], season.ShareYearMenuItems);
        Assert.Equal([false, false, false, false, false, true], season.ShareMenuItemsEnabled); // today's card needs a reading
        var yearOutput = new FakeShareOutput();
        season.ShareOutput = yearOutput;
        await season.ShareAsync(Sharing.ShareAction.PostX, () => season.YearCardAsync(2026));
        Assert.Equal("Image copied. Paste it into the X post that just opened.", season.ShareStatusText.Text);
        Assert.Equal((1200, 630), PngSize(Assert.Single(yearOutput.Copied)));
        Assert.StartsWith("https://x.com/intent/tweet?text=My%202026%20with%20Claude%20Code", Assert.Single(yearOutput.Opened));
        // A cancelled Save leaves no progress line behind.
        await season.ShareAsync(Sharing.ShareAction.Save, () => season.YearCardAsync(2026));
        Assert.False(season.ShareStatusText.IsVisible);
        var yearCard = (await season.YearCardAsync(2026))!;
        Assert.Equal("3k", yearCard.Headline);
        Assert.Contains(new Sharing.ShareStat("Favorite model", "Opus 5.5"), yearCard.Stats);
        var quiet = Flyout(new UsageSnapshot { IsStale = true, FetchedAt = Now }, shareClock: () => December);
        quiet.YearStats = SampleYearStats(empty: true);
        await quiet.ShareAsync(Sharing.ShareAction.Copy, () => quiet.YearCardAsync(2026), Sharing.YearCard.NothingYet(2026));
        Assert.Equal("No Claude Code sessions in 2026 to show yet.", quiet.ShareStatusText.Text);
        quiet.Close();
        season.Close();

        var settings = new AppSettings();
        var settingsWindow = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
        Assert.True(settingsWindow.ShareWideButton.IsChecked);
        Assert.False(settingsWindow.ShareIncludeProjectNamesToggle.IsChecked);
        settingsWindow.SharePortraitButton.IsChecked = true;
        Assert.False(settingsWindow.ShareWideButton.IsChecked);
        // The chosen shape can't be unchecked into no choice.
        settingsWindow.SharePortraitButton.IsChecked = false;
        Assert.True(settingsWindow.SharePortraitButton.IsChecked);
        settingsWindow.ShareIncludeProjectNamesToggle.IsChecked = true;
        settingsWindow.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("portrait", settings.ShareCardShape);
        Assert.True(settings.ShareIncludeProjectNames);
    }

/// <summary>Mid-December 2026: the year card's season. Out of it: mid-November.</summary>
    private static readonly DateTimeOffset December = new(2026, 12, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset November = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The year card's numbers over one sample session (14 March 2026, Opus 5.5) or none,
    /// in a temp folder, read as of mid-December; already read, so the card needs no wait.</summary>
    private static Stats.YearStatsService SampleYearStats(bool empty = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "stickyaiusage-uitests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(folder, "projects", "C--work-acme-webapp");
        Directory.CreateDirectory(project);
        if (!empty)
        {
            const string id = "3d0b8c7e-5f1a-4c2e-9b6d-7a8e9f0a1b2c";
            File.WriteAllText(Path.Combine(project, id + ".jsonl"),
                "{\"type\":\"assistant\",\"sessionId\":\"" + id + "\",\"cwd\":\"/work/acme-webapp\",\"timestamp\":\"2026-03-14T09:00:00Z\"," +
                "\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":2000}}}\n");
        }
        var stats = new Stats.YearStatsService(new TranscriptIndex(Path.Combine(folder, "projects")), Path.Combine(folder, "stats"),
            () => December, TimeZoneInfo.Utc);
        stats.GetAsync(2026).GetAwaiter().GetResult(); // no await: nothing here waits for the UI thread
        return stats;
    }

    /// <summary>A PNG's width and height from its header.</summary>
    private static (int Width, int Height) PngSize(byte[] png) =>
        (png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19], png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23]);

    private sealed class FakeShareOutput : Sharing.IShareOutput
    {
        public readonly List<byte[]> Copied = [];
        public readonly List<string> Opened = [];

        public Task<bool> CopyImageAsync(byte[] png)
        {
            Copied.Add(png);
            return Task.FromResult(true);
        }

        public Task<string?> SaveImageAsync(byte[] png, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SaveToPicturesAsync(byte[] png, string fileName) => Task.FromResult<string?>(null);

        public Task<bool> OpenUrlAsync(string url)
        {
            Opened.Add(url);
            return Task.FromResult(true);
        }
    }

    [AvaloniaFact]
    public void Pace_forecast_is_on_until_switched_off_and_saved()
    {
        var settings = new AppSettings();
        var window = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
        Assert.True(window.ShowForecastToggle.IsChecked);
        window.ShowForecastToggle.IsChecked = false;
        Assert.True(settings.ShowForecast);
        window.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(settings.ShowForecast);
    }

    [AvaloniaFact]
    public void Check_for_updates_is_off_until_switched_on_and_saved()
    {
        var settings = new AppSettings();
        var window = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
        Assert.False(window.CheckForUpdatesToggle.IsChecked);
        window.CheckForUpdatesToggle.IsChecked = true;
        Assert.False(settings.CheckForUpdates); // applies on Save only
        window.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(settings.CheckForUpdates);
    }

    [AvaloniaFact]
    public void Usage_alerts_have_switches_quiet_hours_and_a_test()
    {
        var testAlerts = 0;
        var settings = new AppSettings();
        var window = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { },
            SendTestAlert: () => testAlerts++));
        Assert.True(window.AlertsEnabledToggle.IsChecked);
        Assert.True(window.AlertOnThresholdToggle.IsChecked == true && window.AlertOnForecastToggle.IsChecked == true &&
                    window.AlertOnLimitToggle.IsChecked == true && window.AlertOnResetToggle.IsChecked == true);
        Assert.False(window.QuietHoursToggle.IsChecked);
        Assert.False(window.QuietHoursOptions.IsEnabled);

        window.QuietHoursToggle.IsChecked = true;
        Assert.True(window.QuietHoursOptions.IsEnabled);
        Assert.Equal(("22:00", "07:00"), (window.QuietFromBox.Text, window.QuietToBox.Text));
        window.QuietToBox.Text = "later";
        window.ValidateQuietHours();
        Assert.False(window.SaveButton.IsEnabled);
        Assert.True(window.QuietHoursError.IsVisible);
        window.QuietToBox.Text = "6:30";
        window.ValidateQuietHours();
        Assert.True(window.SaveButton.IsEnabled);
        window.AlertOnResetToggle.IsChecked = false;
        window.AlertsEnabledToggle.IsChecked = false;
        Assert.False(window.AlertOptions.IsEnabled);
        window.AlertsEnabledToggle.IsChecked = true;

        window.TestAlertButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, testAlerts);
        Assert.Null(settings.AlertQuietFrom); // applies on Save only
        window.Show();
        window.AlertsCard.BringIntoView(); // below the fold: scroll the section into the picture
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Save(window, "mac-40-settings-alerts.png", background: null);
        window.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(("22:00", "06:30"), (settings.AlertQuietFrom, settings.AlertQuietTo));
        Assert.False(settings.AlertOnReset);
        Assert.True(settings.AlertsEnabled);
    }

    [AvaloniaFact]
    public void A_refresh_s_alerts_go_to_the_app()
    {
        var widget = Pill(Plan(), compact: false);
        var raised = new List<StickyAIusage.Alerts.Alert>();
        widget.AlertsRaised += alerts => raised.AddRange(alerts);
        widget.RaiseAlerts([]);
        Assert.Empty(raised);
        widget.RaiseAlerts([StickyAIusage.Alerts.AlertPlanner.TestAlert()]);
        Assert.Equal("Test alert", Assert.Single(raised).Title);
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
        window.Show();
        window.CaptureRenderedFrame();
        // The live preview shows the chips being arranged (flyout cards start folded; the preview never does).
        Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.PreviewCard)
            .OfType<StickyAIusage.Mac.Controls.ChevronSegment>(), s => s.IsEffectivelyVisible);
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

    /// <summary>Codex beside Claude: a reading as Codex's session log gives it.</summary>
    private static ProviderReading CodexReading(double fiveHour = 91, double weekly = 34, UsageHealth health = UsageHealth.Ok) =>
        new(new ProviderUsage("codex", "Codex", "Plus",
            [
                new UsageWindow(UsageWindowKinds.FiveHour, fiveHour, Now.AddMinutes(95), TimeSpan.FromHours(5)),
                new UsageWindow(UsageWindowKinds.SevenDay, weekly, Now.AddDays(3), TimeSpan.FromDays(7))
            ], null, Now.AddMinutes(-2), CodexUsageProvider.SourceIdValue, SourceTrust.Derived, health, null)
        {
            WholePointsOnly = true,
            SourceLabel = CodexUsageProvider.SourceLabelText,
            WindowLabels = new Dictionary<string, string> { ["five_hour"] = "5-hour window", ["seven_day"] = "Weekly" }
        }, []);

    private static ProviderReading CodexWithoutLimits() =>
        new(new ProviderUsage("codex", "Codex", null, [], null, Now, CodexUsageProvider.SourceIdValue, SourceTrust.Derived,
            UsageHealth.Unavailable, CodexUsageProvider.NoLimitsDetail) { SourceLabel = CodexUsageProvider.SourceLabelText }, []);

    [AvaloniaFact]
    public void Codex_beside_claude_renders_its_section_and_the_pill_chip()
    {
        Directory.CreateDirectory(OutDir);
        var window = new WidgetWindow(new AppSettings(), live: false);
        window.ApplyUsage(Plan(), Session(), others: [CodexReading()]);
        window.ToggleExpanded();
        window.ShowSessions(SampleCards().Take(1).ToList());
        window.Show();
        window.CaptureRenderedFrame();
        var texts = VisibleTexts(window);
        Assert.Contains("Codex · Plus", texts);
        Assert.Contains("from Codex's local session log", texts);
        Assert.Contains(texts, t => t.StartsWith("91% · Resets in 1h 3"));
        Assert.Contains(texts, t => t.StartsWith("34% · Resets "));
        Assert.Equal(["Codex 91%"], window.ExtraChipTexts);
        Assert.Contains("Codex 91%", texts);
        Save(window, "mac-47-flyout-codex.png");

        // Codex below orange: the section stays, the chip goes.
        var calm = new WidgetWindow(new AppSettings(), live: false);
        calm.ApplyUsage(Plan(), Session(), others: [CodexReading(fiveHour: 20)]);
        Assert.Empty(calm.ExtraChipTexts);
        calm.Close();

        // A mode without limits: the message, no rows; without Codex: no section at all.
        var none = new WidgetWindow(new AppSettings(), live: false);
        none.ApplyUsage(Plan(), Session(), others: [CodexWithoutLimits()]);
        none.ToggleExpanded();
        none.Show();
        none.CaptureRenderedFrame();
        Assert.Contains(CodexUsageProvider.NoLimitsDetail, VisibleTexts(none));
        Save(none, "mac-48-flyout-codex-no-limits.png");
        var without = new WidgetWindow(new AppSettings(), live: false);
        without.ApplyUsage(Plan(), Session());
        without.ToggleExpanded();
        without.Show();
        without.CaptureRenderedFrame();
        Assert.DoesNotContain(VisibleTexts(without), t => t.Contains("Codex"));
        without.Close();

        Save(Pill(Plan(), compact: true, others: [CodexReading()]), "mac-49-pill-codex-chip-menubar.png");

        StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark: false);
        try
        {
            var light = new WidgetWindow(new AppSettings(), live: false);
            light.ApplyUsage(Plan(), Session(), others: [CodexReading()]);
            light.ToggleExpanded();
            light.Show();
            light.CaptureRenderedFrame();
            Save(light, "mac-50-flyout-codex-light.png", background: Paper);
        }
        finally
        {
            StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark: true);
        }
    }

    [AvaloniaFact]
    public void A_newer_version_shows_a_line_at_the_foot_of_the_flyout_with_its_update_command()
    {
        var notice = new Updates.UpdateNotice(Updates.SemanticVersion.Parse("2.0.1")!, Updates.SemanticVersion.Parse("2.0.0")!,
            Updates.UpdateChecker.ReleasePageBase + "v2.0.1", Updates.InstallMethod.Script,
            Updates.InstallMethods.UpdateCommand(Updates.InstallMethod.Script, isMac: true, preRelease: false), "Terminal");
        var window = new WidgetWindow(new AppSettings(), live: false);
        window.ApplyUsage(Plan(), Session());
        window.ApplyUpdate(notice);
        window.ToggleExpanded();
        window.Show();
        window.CaptureRenderedFrame();

        var texts = VisibleTexts(window);
        Assert.Contains("Update available: v2.0.1 — what's new", texts);
        Assert.Contains("To update, run this in Terminal:", texts);
        Assert.Contains("curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh", texts);
        Assert.Equal(notice, window.LatestUpdate);
        Save(window, "mac-58-flyout-update.png");

        window.ApplyUpdate(notice with { Method = Updates.InstallMethod.Download, Command = null, CommandPlace = null });
        Assert.False(window.UpdateCommandRow.IsVisible);
        Assert.Equal("Download it from the release page.", window.UpdateHowText.Text);
        window.ApplyUpdate(null);
        Assert.False(window.UpdatePanel.IsVisible);
        Assert.Null(window.LatestUpdate);

        var none = new WidgetWindow(new AppSettings(), live: false);
        Assert.False(none.UpdatePanel.IsVisible);
        none.Close();
    }

    [AvaloniaFact]
    public void Settings_has_a_codex_switch_that_says_whether_codex_is_here()
    {
        var actions = new SettingsActions(() => { }, () => { }, () => { }, () => { });
        var probe = SettingsWindow.CodexPresent;
        try
        {
            SettingsWindow.CodexPresent = () => false;
            var absent = new SettingsWindow(new AppSettings(), actions);
            Assert.Equal(OtherProviders.CodexSettingsHint(false), absent.CodexHintText.Text);
            Assert.True(absent.ShowCodexToggle.IsChecked);
            absent.Close();

            SettingsWindow.CodexPresent = () => true;
            var settings = new AppSettings();
            var present = new SettingsWindow(settings, actions);
            Assert.Equal(OtherProviders.CodexSettingsHint(true), present.CodexHintText.Text);
            Save(present, "mac-51-settings-codex.png", background: null);
            var toggled = new SettingsWindow(settings, actions);
            toggled.Show();
            toggled.ShowCodexToggle.IsChecked = false;
            toggled.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(settings.IsProviderEnabled(CodexUsageProvider.Id));
        }
        finally
        {
            SettingsWindow.CodexPresent = probe;
        }
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
        Assert.False(plan.DayTargetSection.IsVisible);
        Assert.True(plan.DayTargetNote.IsVisible);
        plan.DayTargetBox.Text = "0";
        Assert.True(plan.SaveButton.IsEnabled);
        Save(plan, "mac-26-settings-plan-day-target-off.png", background: null);

        // Usage source: a new install shows the plugin with its install line;
        // Legacy shows the unofficial warning and applies only on Save.
        var connectedProbe = SettingsWindow.PluginConnected;
        SettingsWindow.PluginConnected = () => false;
        var feed = new SettingsWindow(new AppSettings(), actions);
        Assert.True(feed.PluginInstallRow.IsVisible);
        Assert.False(feed.SourceWarning.IsVisible);
        feed.Close();
        // Once the plugin has connected the install line goes.
        SettingsWindow.PluginConnected = () => true;
        var connected = new SettingsWindow(new AppSettings(), actions);
        Assert.False(connected.PluginInstallRow.IsVisible);
        connected.Close();
        SettingsWindow.PluginConnected = connectedProbe;
        var legacySettings = new AppSettings();
        var legacy = new SettingsWindow(legacySettings, actions);
        // Checked the way VoiceOver toggles it: no Click.
        var legacySegment = legacy.SourceSegments.Children.OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .Single(b => Equals(b.Tag, ClaudeSourceMode.Legacy));
        legacySegment.IsChecked = true;
        legacySegment.IsChecked = false; // unchecking the chosen one keeps it chosen
        Assert.True(legacySegment.IsChecked);
        Assert.True(legacy.SourceWarning.IsVisible);
        Assert.False(legacy.PluginInstallRow.IsVisible);
        Assert.Equal(UsageSourceOptions.UnofficialWarning, legacy.SourceWarning.Text);
        Save(legacy, "mac-31-settings-source-legacy.png", background: null);
        Assert.Equal("feed", legacySettings.ClaudeSource);
    }

    private static WidgetWindow Pill(UsageSnapshot usage, bool compact, bool paused = false,
        decimal? spentToday = null, AppSettings? settings = null, IReadOnlyList<PaceForecast>? forecasts = null,
        IReadOnlyList<ProviderReading>? others = null)
    {
        var window = new WidgetWindow(settings ?? new AppSettings(), live: false);
        window.ApplyUsage(usage, Session(), spentToday, forecasts, others);
        window.SetCompact(compact);
        window.SetRunning(!paused);
        return window;
    }

    /// <param name="shareClock">What the Share menu takes as now; mid-November (no year card) unless given.</param>
    private static WidgetWindow Flyout(UsageSnapshot usage, bool show = false, decimal? spentToday = null, Func<DateTimeOffset>? shareClock = null)
    {
        var window = new WidgetWindow(new AppSettings(), live: false) { ShareClock = shareClock ?? (() => November) };
        window.ApplyUsage(usage, Session(), spentToday);
        if (show)
            window.Show();
        window.ToggleExpanded();
        window.ShowSessions(SampleCards());
        return window;
    }

    /// <summary>Answers --version and the install from a script.</summary>
    private sealed class ScriptedCli(CliRun install) : IClaudeCli
    {
        public List<string> Ran { get; } = [];

        public Task<CliRun> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Ran.Add(string.Join(' ', arguments));
            return Task.FromResult(arguments[0] == "--version" ? new CliRun(0, "2.1.325 (Claude Code)", "") : install);
        }
    }

    private static ConnectSession NoCli() =>
        new(() => null, () => false, new ScriptedCli(new CliRun(0, "", "")), null, PluginInstaller.DefaultSource);

    [AvaloniaFact]
    public async Task The_first_run_window_walks_three_steps_and_connects_live()
    {
        foreach (var dark in new[] { true, false })
        {
            StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark);
            var theme = dark ? "dark" : "light";
            for (var step = 1; step <= 3; step++)
            {
                var window = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun, NoCli(), new OnboardingActions(() => { }, _ => { }));
                window.ShowStep(step);
                Assert.Equal(Onboarding.StepCaption(step), window.StepCaptionText.Text);
                Assert.Equal(step == 3 ? "Finish" : "Next", window.NextButton.Content);
                Assert.Equal(step > 1, window.BackButton.IsVisible);
                if (step == 2)
                {
                    Assert.False(window.RunButton.IsEnabled);
                    Assert.StartsWith(Onboarding.NoCliHint, window.CopyHintText.Text);
                    Assert.Equal(PluginInstaller.ShellCommand(PluginInstaller.DefaultSource), window.CommandText.Text);
                }
                if (step == 3)
                    Assert.Equal("Launch at login", window.StartAtLoginLabel.Text);
                Save(window, $"mac-52-first-run-step{step}-{theme}.png");
            }

            UsageSnapshot? usage = null;
            var wrote = false;
            var cli = new ScriptedCli(new CliRun(0, """{"outcome":"ok","message":"Successfully installed plugin: sticky-usage@sticky-ai-usage (scope: user)"}""", ""));
            var connect = new ConnectSession(() => usage, () => wrote, cli, "claude", PluginInstaller.DefaultSource);
            var waiting = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun, connect, new OnboardingActions(() => { }, _ => { }));
            waiting.ShowStep(2);
            await connect.RunAsync();
            Assert.Equal(["--version", "plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage --json"], cli.Ran);
            Assert.Equal(ConnectFlow.InstalledWaiting, waiting.StatusText.Text);
            Assert.True(waiting.StatusBorder.IsVisible);
            Save(waiting, $"mac-53-connect-waiting-{theme}.png");

            var connected = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun, connect, new OnboardingActions(() => { }, _ => { }));
            connected.ShowStep(2);
            wrote = true;
            usage = new UsageSnapshot
            {
                Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 43, WeeklyUtilizationPercent = 12, FetchedAt = Now,
                SourceId = ClaudeFeedUsageSource.Id, Trust = SourceTrust.Official
            };
            connect.Refresh();
            Assert.Equal("Connected: 5-hour 43% · weekly 12%.", connected.StatusText.Text);
            Assert.Equal(StickyAIusage.Mac.Services.ThemeService.Resource<IBrush>("GoodBrush"), connected.StatusDot.Fill);
            Save(connected, $"mac-54-connect-connected-{theme}.png");

            var refused = new ConnectSession(() => null, () => false,
                new ScriptedCli(new CliRun(1, "", "error: managed settings do not allow this marketplace")), "claude", PluginInstaller.DefaultSource);
            var failed = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun, refused, new OnboardingActions(() => { }, _ => { }));
            failed.ShowStep(2);
            await refused.RunAsync();
            Assert.Contains("organization may not allow user plugins", failed.StatusText.Text);
            Assert.True(failed.OutputBox.IsVisible);
            failed.LegacyLinkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(failed.LegacyPanel.IsVisible);
            Save(failed, $"mac-55-connect-failed-{theme}.png");

            var whatsNew = new OnboardingWindow(new AppSettings(), OnboardingScreen.WhatsNew, NoCli(), new OnboardingActions(() => { }, _ => { }));
            Assert.Equal((2, "Done", Onboarding.WhatsNewTitle), (whatsNew.Step, whatsNew.NextButton.Content, whatsNew.WindowTitleText.Text));
            Assert.False(whatsNew.ReadsPanel.IsVisible);
            Save(whatsNew, $"mac-56-whats-new-{theme}.png");
        }
        StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark: true);

        // Finish applies the three switches and marks the window done.
        var chosen = new AppSettings();
        bool? startAtLogin = null;
        var finish = new OnboardingWindow(chosen, OnboardingScreen.FirstRun, NoCli(), new OnboardingActions(() => { }, on => startAtLogin = on));
        finish.Show();
        Assert.True(finish.StartAtLoginCheck.IsChecked);
        finish.AlertsCheck.IsChecked = false;
        finish.Finish();
        Assert.Equal((true, Onboarding.CurrentVersion, false, false, true), (chosen.OnboardingCompleted, chosen.OnboardingVersion,
            chosen.CheckForUpdates, chosen.AlertsEnabled, chosen.StartWithWindows));
        Assert.Equal((true, true), (startAtLogin, finish.Finished));

        var legacy = new AppSettings();
        var legacyChanged = 0;
        var legacyWindow = new OnboardingWindow(legacy, OnboardingScreen.FirstRun, NoCli(), new OnboardingActions(() => legacyChanged++, _ => { }));
        legacyWindow.ShowStep(2);
        legacyWindow.LegacyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(("legacy", 1, false), (legacy.ClaudeSource, legacyChanged, legacy.OnboardingCompleted));
        legacyWindow.Close();

        var fromHealth = new AppSettings();
        var connectOnly = new OnboardingWindow(fromHealth, OnboardingScreen.Connect, NoCli(),
            new OnboardingActions(() => { }, _ => throw new InvalidOperationException("Connect alone must not change launch at login")));
        connectOnly.Show();
        connectOnly.Finish();
        Assert.False(fromHealth.OnboardingCompleted);
    }

    [AvaloniaFact]
    public async Task Settings_lists_health_with_one_button_each_and_a_report()
    {
        var facts = new HealthFacts
        {
            Now = Now, IsMac = true, ClaudeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            ClaudeDirectoryExists = true, TranscriptCount = 41, ClaudeCodeVersion = "2.1.325", SessionsToday = 3,
            Usage = new UsageSnapshot { IsStale = true, FetchedAt = Now, StatusDetail = ClaudeFeedUsageSource.WaitingDetail }
        };
        var items = HealthCheck.Build(facts);
        foreach (var dark in new[] { true, false })
        {
            StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark);
            var ran = new List<string>();
            var settings = new AppSettings();
            var window = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { },
                CheckHealth: () => Task.FromResult(new HealthResult(items, HealthCheck.Report(items, "test", Now))),
                RunHealthAction: id => { ran.Add(id); if (id == HealthActionIds.ToggleStartup) settings.StartWithWindows = true; }));
            window.Show();
            await window.RefreshHealthAsync();
            Assert.Equal(10, window.HealthItems.Count);
            Assert.Equal("2 things need a look.", window.HealthNoteText.Text);
            Assert.True(window.CopyReportButton.IsEnabled);
            Assert.True(window.HealthCard.IsVisible);
            Save(window, $"mac-57-settings-health-{(dark ? "dark" : "light")}.png");

            window = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { },
                CheckHealth: () => Task.FromResult(new HealthResult(items, HealthCheck.Report(items, "test", Now))),
                RunHealthAction: id => { ran.Add(id); if (id == HealthActionIds.ToggleStartup) settings.StartWithWindows = true; }));
            window.Show();
            await window.RefreshHealthAsync();
            window.CaptureRenderedFrame(); // lays the items out
            // A click on the item's own button (through the template) reaches the app.
            var connectButton = window.HealthList.GetVisualDescendants().OfType<Button>().First(b => b.Tag as string == HealthActionIds.Connect);
            Assert.Equal("Connect", connectButton.Content);
            connectButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            window.LaunchAtLoginToggle.IsChecked = true; // unsaved
            await window.RunHealthActionAsync(HealthActionIds.OpenUsageSource);
            await window.RunHealthActionAsync(HealthActionIds.SendTestAlert);
            Assert.True(window.LaunchAtLoginToggle.IsChecked); // another button leaves the unsaved switch alone
            window.LaunchAtLoginToggle.IsChecked = false;
            await window.RunHealthActionAsync(HealthActionIds.ToggleStartup);
            Assert.Equal([HealthActionIds.Connect, HealthActionIds.SendTestAlert, HealthActionIds.ToggleStartup], ran);
            Assert.True(window.LaunchAtLoginToggle.IsChecked);
            Assert.Equal(StickyAIusage.Mac.Services.ThemeService.Resource<IBrush>("DangerBrush"), SettingsWindow.HealthDotBrush.Convert(
                HealthStatus.Fail, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
            window.Close();
        }
        StickyAIusage.Mac.Services.ThemeService.ApplyForTests(dark: true);

        // Without a health check (a test or designer window) the section stays hidden.
        var plain = new SettingsWindow(new AppSettings(), new SettingsActions(() => { }, () => { }, () => { }, () => { }));
        Assert.False(plain.HealthCard.IsVisible);
        plain.Close();
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

    /// <summary>The 5-hour limit 25 minutes away (red), the weekly window
    /// ahead of an even pace.</summary>
    /// <summary>A time of day on today's date, in the local zone the pill formats times in.</summary>
    private static DateTimeOffset LocalTimeToday(int hour, int minute) =>
        new(DateTime.Today.AddHours(hour).AddMinutes(minute));

    private static IReadOnlyList<PaceForecast> PlanForecasts(DateTimeOffset? limitAt = null) =>
    [
        new(UsageWindowKinds.FiveHour, 88, 28.8, limitAt ?? Now.AddMinutes(25), 150, 40, ForecastConfidence.High, "the last 30 minutes")
            { ResetsAt = Now.AddMinutes(126), IsActive = true },
        new(UsageWindowKinds.SevenDay, 14, 0.4, null, 70, 6.3, ForecastConfidence.Low, "the last 24 hours")
            { ResetsAt = Now.AddDays(5).AddHours(3), IsActive = true }
    ];

    /// <summary>A monthly cap heading past its limit: 106% → $212 of $200.</summary>
    private static PaceForecast MonthForecast() =>
        new(UsageWindowKinds.MonthSpend, 48.4, 0.1, null, 106, 4, ForecastConfidence.High, "the last 7 days")
            { ResetsAt = Now.AddDays(10), IsActive = true };

    private static UsageSnapshot Plan(bool credits = false, double weekly = 14, TimeSpan? staleFor = null, double fiveHour = 43) => new()
    {
        Mode = UsageMode.PlanLimits,
        SubscriptionType = "pro",
        FiveHourUtilizationPercent = fiveHour,
        FiveHourResetsAt = Now.AddMinutes(126),
        WeeklyUtilizationPercent = weekly,
        WeeklyResetsAt = Now.AddDays(5).AddHours(3),
        ExtraUsageEnabled = credits,
        CreditBalance = credits ? 9.32m : null,
        FetchedAt = Now - (staleFor ?? TimeSpan.FromSeconds(20)),
        IsStale = staleFor is not null
    };

    private static UsageSnapshot FeedPlan(TimeSpan age) => Plan() with
    {
        SubscriptionType = null,
        FetchedAt = Now - age,
        SourceId = ClaudeFeedUsageSource.Id,
        Trust = SourceTrust.Official
    };

    private static UsageSnapshot FeedSpendLimit() => new()
    {
        Mode = UsageMode.MonthlySpend,
        HasMonthlySpend = true,
        MonthlySpendPercentOnly = true,
        MonthlySpendPercent = 63.4,
        MonthlySpendResetsAt = Now.AddDays(14),
        FetchedAt = Now.AddSeconds(-20),
        SourceId = ClaudeFeedUsageSource.Id,
        Trust = SourceTrust.Official
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
        TimeSpan? ran = null, TimeSpan? idle = null, GitInfo? git = null, IReadOnlyList<string>? skills = null,
        bool rough = false) => new()
    {
        CostIsRough = rough,
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
