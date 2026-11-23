using System.Globalization;
using System.Text.Json;
using StickyAIusage.Alerts;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Every alert kind, its repeat rule, and what silences it, with an
/// injected clock (UTC zone, en-US times).</summary>
public class AlertPlannerTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveHourReset = T0.AddHours(2.5); // 4:30 PM
    private static readonly DateTimeOffset WeekReset = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero); // Fri 10:00 AM

    private static UsageWindow FiveHour(double percent, DateTimeOffset? reset = null) =>
        new(UsageWindowKinds.FiveHour, percent, reset ?? FiveHourReset, TimeSpan.FromHours(5));

    private static UsageWindow Weekly(double percent) =>
        new(UsageWindowKinds.SevenDay, percent, WeekReset, TimeSpan.FromDays(7));

    private static ProviderUsage Usage(params UsageWindow[] windows) => Usage("claude", "Claude", null, windows);

    private static ProviderUsage Usage(string provider, string name, SpendCap? spend, params UsageWindow[] windows) =>
        new(provider, name, null, windows, spend, T0, "test", SourceTrust.Official, UsageHealth.Ok, null);

    private static PaceForecast Forecast(string kind, double percent, DateTimeOffset? reset, DateTimeOffset? hits,
        ForecastConfidence confidence = ForecastConfidence.Low, double? projected = null) =>
        new(kind, percent, 10, hits, projected, null, confidence, "the last 30 minutes") { ResetsAt = reset, IsActive = true };

    /// <summary>Plans one reading after another, keeping the state, like the app does.</summary>
    private sealed class Run(AlertSettings? settings = null)
    {
        public AlertSettings Settings { get; set; } = settings ?? AlertSettings.Default;
        public AlertState State { get; set; } = AlertState.Empty;
        public IReadOnlyList<Alert> Dropped { get; private set; } = [];

        public IReadOnlyList<Alert> Plan(DateTimeOffset now, ProviderUsage usage, params PaceForecast[] forecasts) =>
            Plan(now, [new AlertInput(usage, forecasts)]);

        public IReadOnlyList<Alert> Plan(DateTimeOffset now, IReadOnlyList<AlertInput> inputs)
        {
            var plan = AlertPlanner.Plan(inputs, Settings, State, now, Zone, EnUs);
            State = plan.State;
            Dropped = plan.Dropped;
            return plan.Alerts;
        }
    }

    // ---------------------------------------------------------- Threshold

    [Fact]
    public void A_threshold_fires_once_per_window_and_threshold()
    {
        var run = new Run();

        Assert.Empty(run.Plan(T0, Usage(FiveHour(79.6))));
        var orange = Assert.Single(run.Plan(T0.AddMinutes(1), Usage(FiveHour(81))));
        Assert.Equal(AlertKind.Threshold, orange.Kind);
        Assert.Equal("5-hour window at 81%", orange.Title);
        Assert.Equal("Resets today 4:30 PM.", orange.Body);
        Assert.Equal(UsageLevel.Orange, orange.Level);
        Assert.Empty(run.Plan(T0.AddMinutes(2), Usage(FiveHour(84))));
        Assert.Empty(run.Plan(T0.AddMinutes(3), Usage(FiveHour(79)))); // dips below and back: no repeat
        Assert.Empty(run.Plan(T0.AddMinutes(4), Usage(FiveHour(82))));

        var red = Assert.Single(run.Plan(T0.AddMinutes(5), Usage(FiveHour(96))));
        Assert.Equal("5-hour window at 96%", red.Title);
        Assert.Equal(UsageLevel.Red, red.Level);
        Assert.Empty(run.Plan(T0.AddMinutes(6), Usage(FiveHour(97))));
    }

    [Fact]
    public void Yellow_never_notifies()
    {
        var run = new Run();
        Assert.Empty(run.Plan(T0, Usage(FiveHour(60), Weekly(70))));
    }

    [Fact]
    public void A_percentage_is_never_rounded_up_to_a_threshold_it_has_not_reached()
    {
        var run = new Run(AlertSettings.Default with { OrangePercent = 79.5 });
        Assert.Equal("5-hour window at 79%", Assert.Single(run.Plan(T0, Usage(FiveHour(79.6)))).Title);
    }

    [Fact]
    public void Started_above_a_threshold_it_notifies_once_as_news_to_this_run()
    {
        var run = new Run();
        var alert = Assert.Single(run.Plan(T0, Usage(FiveHour(88))));
        Assert.Equal("5-hour window at 88%", alert.Title);
        Assert.Empty(run.Plan(T0.AddSeconds(15), Usage(FiveHour(88))));
    }

    [Fact]
    public void Each_window_has_its_own_thresholds()
    {
        var run = new Run();
        var alerts = run.Plan(T0, Usage(FiveHour(85), Weekly(81)));
        Assert.Equal(["5-hour window at 85%", "Weekly window at 81%"], alerts.Select(a => a.Title));
        Assert.Equal("Resets Fri 10:00 AM.", alerts[1].Body);
    }

    [Fact]
    public void A_threshold_edited_in_Settings_is_a_new_threshold()
    {
        var run = new Run();
        Assert.Single(run.Plan(T0, Usage(FiveHour(85))));

        run.Settings = run.Settings with { OrangePercent = 84 };
        Assert.Single(run.Plan(T0.AddMinutes(1), Usage(FiveHour(85))));
        Assert.Empty(run.Plan(T0.AddMinutes(2), Usage(FiveHour(85))));
    }

    [Fact]
    public void A_jump_to_the_limit_is_one_notification_not_three()
    {
        var run = new Run();
        var alert = Assert.Single(run.Plan(T0, Usage(FiveHour(70))).Concat(run.Plan(T0.AddMinutes(1), Usage(FiveHour(100)))));
        Assert.Equal(AlertKind.LimitReached, alert.Kind);
        Assert.Empty(run.Plan(T0.AddMinutes(2), Usage(FiveHour(100))));
    }

    [Fact]
    public void Readings_that_date_one_reset_seconds_apart_across_a_minute_are_one_window()
    {
        var run = new Run();
        Assert.Single(run.Plan(T0, Usage(FiveHour(85, FiveHourReset.AddSeconds(-10)))));
        Assert.Empty(run.Plan(T0.AddMinutes(1), Usage(FiveHour(86, FiveHourReset.AddSeconds(20)))));
    }

    [Fact]
    public void A_window_whose_reset_has_passed_has_started_over()
    {
        var run = new Run();
        Assert.Empty(run.Plan(FiveHourReset.AddSeconds(1), Usage(FiveHour(90))));
    }

    [Fact]
    public void A_reading_that_needs_a_sign_in_notifies_nothing()
    {
        var run = new Run();
        var usage = Usage(FiveHour(90)) with { Health = UsageHealth.NeedsSignIn };
        Assert.Empty(run.Plan(T0, usage));
    }

    [Fact]
    public void The_monthly_cap_names_its_dollars()
    {
        var run = new Run();
        var month = new UsageWindow(UsageWindowKinds.MonthSpend, 82, null, null);
        var usage = Usage("claude", "Claude", new SpendCap(164m, 200m, 82, null, "month"), month);

        var alert = Assert.Single(run.Plan(T0, usage));
        Assert.Equal("Monthly spend at 82%", alert.Title);
        Assert.Equal("$164.00 of your $200.00 cap.", alert.Body);
        // Its series is the assumed month: the start of December.
        Assert.Contains("|month_spend|2026-12-01T00:00Z|Threshold|80", alert.Key);
        Assert.Empty(run.Plan(T0.AddDays(1), usage));
    }

    // ---------------------------------------------------------- LimitReached

    [Fact]
    public void The_limit_fires_once_per_window()
    {
        var run = new Run();
        var alert = Assert.Single(run.Plan(T0, Usage(FiveHour(100))));
        Assert.Equal(AlertKind.LimitReached, alert.Kind);
        Assert.Equal("5-hour limit reached", alert.Title);
        Assert.Equal("Resets today 4:30 PM.", alert.Body);
        Assert.Equal(UsageLevel.Red, alert.Level);
        Assert.Empty(run.Plan(T0.AddMinutes(5), Usage(FiveHour(100))));
    }

    [Fact]
    public void With_the_limit_switch_off_the_red_threshold_still_speaks()
    {
        var run = new Run(AlertSettings.Default with { OnLimit = false });
        Assert.Equal(AlertKind.Threshold, Assert.Single(run.Plan(T0, Usage(FiveHour(100)))).Kind);
    }

    // ---------------------------------------------------------- ForecastHit

    [Fact]
    public void A_limit_forecast_within_the_lead_time_fires_once()
    {
        var run = new Run();
        var hits = T0.AddMinutes(28);

        Assert.Empty(run.Plan(T0.AddMinutes(-10), Usage(FiveHour(60)),
            Forecast(UsageWindowKinds.FiveHour, 60, FiveHourReset, T0.AddMinutes(45)))); // 55 min away: not yet
        var alert = Assert.Single(run.Plan(T0, Usage(FiveHour(70)), Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, hits)));
        Assert.Equal(AlertKind.ForecastHit, alert.Kind);
        Assert.Equal("5-hour limit at about 2:28 PM", alert.Title);
        Assert.Equal("At this pace you hit the 5-hour limit at 2:28 PM (in 28m). Now at 70%, resets today 4:30 PM.", alert.Body);
        Assert.Empty(run.Plan(T0.AddMinutes(1), Usage(FiveHour(72)), Forecast(UsageWindowKinds.FiveHour, 72, FiveHourReset, hits)));
    }

    [Fact]
    public void A_flapping_forecast_comes_back_only_after_fifteen_clear_minutes()
    {
        var run = new Run();
        PaceForecast Soon(DateTimeOffset now) => Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, now.AddMinutes(20));
        PaceForecast Clear() => Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, null);

        Assert.Single(run.Plan(T0, Usage(FiveHour(70)), Soon(T0)));
        Assert.Empty(run.Plan(T0.AddMinutes(1), Usage(FiveHour(70)), Clear()));
        Assert.Empty(run.Plan(T0.AddMinutes(10), Usage(FiveHour(70)), Soon(T0.AddMinutes(10)))); // back after 9: still told
        Assert.Empty(run.Plan(T0.AddMinutes(11), Usage(FiveHour(70)), Clear()));
        Assert.Empty(run.Plan(T0.AddMinutes(25), Usage(FiveHour(70)), Clear())); // clear for 14 minutes
        Assert.Empty(run.Plan(T0.AddMinutes(26), Usage(FiveHour(70)), Clear())); // 15: re-armed
        Assert.Equal(AlertKind.ForecastHit,
            Assert.Single(run.Plan(T0.AddMinutes(27), Usage(FiveHour(71)), Soon(T0.AddMinutes(27)))).Kind);
    }

    [Fact]
    public void A_forecast_below_the_confidence_floor_or_without_confidence_is_not_an_alert()
    {
        var run = new Run(AlertSettings.Default with { ForecastFloor = ForecastConfidence.High });
        Assert.Empty(run.Plan(T0, Usage(FiveHour(70)), Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, T0.AddMinutes(20))));

        var none = new Run();
        Assert.Empty(none.Plan(T0, Usage(FiveHour(70)),
            Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, T0.AddMinutes(20), ForecastConfidence.None)));
    }

    [Fact]
    public void A_forecast_alert_at_a_threshold_says_both_in_one()
    {
        var run = new Run();
        var alert = Assert.Single(run.Plan(T0, Usage(FiveHour(85)),
            Forecast(UsageWindowKinds.FiveHour, 85, FiveHourReset, T0.AddMinutes(12))));
        Assert.Equal(AlertKind.ForecastHit, alert.Kind);
        Assert.Contains("Now at 85%", alert.Body);
        // The orange threshold counts as told; red is still to come.
        Assert.Empty(run.Plan(T0.AddMinutes(1), Usage(FiveHour(86))));
        Assert.Equal(AlertKind.Threshold, Assert.Single(run.Plan(T0.AddMinutes(9), Usage(FiveHour(95)))).Kind);
    }

    [Fact]
    public void A_forecast_for_another_window_is_not_used()
    {
        var run = new Run();
        Assert.Empty(run.Plan(T0, Usage(FiveHour(70)),
            Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset.AddHours(5), T0.AddMinutes(20))));
    }

    // ---------------------------------------------------------- WindowReset

    [Fact]
    public void A_busy_window_says_you_are_back_a_minute_after_its_reset()
    {
        var run = new Run();
        run.Plan(T0, Usage(FiveHour(92)));

        // The feed still holds the old window; its reset has passed.
        Assert.Empty(run.Plan(FiveHourReset.AddSeconds(30), Usage(FiveHour(92))));
        var back = Assert.Single(run.Plan(FiveHourReset.AddSeconds(61), Usage(FiveHour(92))));
        Assert.Equal(AlertKind.WindowReset, back.Kind);
        Assert.Equal("You're back", back.Title);
        Assert.Equal("The 5-hour window has reset.", back.Body);
        Assert.Equal(UsageLevel.Green, back.Level);
        Assert.Empty(run.Plan(FiveHourReset.AddMinutes(2), Usage(FiveHour(92))));
    }

    [Fact]
    public void The_first_reading_of_the_new_window_says_it_at_once()
    {
        var run = new Run();
        run.Plan(T0, Usage(FiveHour(85)));
        var back = Assert.Single(run.Plan(FiveHourReset.AddSeconds(5), Usage(FiveHour(3, FiveHourReset.AddHours(5)))));
        Assert.Equal(AlertKind.WindowReset, back.Kind);
    }

    [Fact]
    public void A_quiet_window_resets_without_a_word()
    {
        var run = new Run();
        run.Plan(T0, Usage(FiveHour(60)));
        Assert.Empty(run.Plan(FiveHourReset.AddMinutes(2), Usage(FiveHour(60))));
    }

    [Fact]
    public void A_reset_noticed_more_than_an_hour_late_is_old_news()
    {
        var run = new Run();
        run.Plan(T0, Usage(FiveHour(90)));
        Assert.Empty(run.Plan(FiveHourReset.AddMinutes(61), Usage(FiveHour(4, FiveHourReset.AddHours(5)))));
    }

    [Fact]
    public void A_reset_in_the_middle_of_a_run_starts_the_thresholds_again()
    {
        var run = new Run();
        Assert.Single(run.Plan(T0, Usage(FiveHour(90))));
        var next = FiveHourReset.AddHours(5);
        Assert.Equal(AlertKind.WindowReset, Assert.Single(run.Plan(FiveHourReset.AddSeconds(10), Usage(FiveHour(2, next)))).Kind);
        Assert.Empty(run.Plan(FiveHourReset.AddMinutes(30), Usage(FiveHour(40, next))));
        var again = Assert.Single(run.Plan(FiveHourReset.AddHours(2), Usage(FiveHour(81, next))));
        Assert.Equal("5-hour window at 81%", again.Title);
    }

    [Fact]
    public void A_reset_is_announced_even_when_the_new_reading_has_no_numbers()
    {
        var run = new Run();
        run.Plan(T0, Usage(FiveHour(90)));
        var empty = Usage() with { Health = UsageHealth.Unavailable };
        Assert.Equal(AlertKind.WindowReset, Assert.Single(run.Plan(FiveHourReset.AddSeconds(61), empty)).Kind);
    }

    // ---------------------------------------------------------- WeeklyRunway

    [Fact]
    public void The_weekly_runway_warns_at_most_once_a_day()
    {
        var run = new Run();
        var runsOut = new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero); // Thu 3:00 PM
        PaceForecast Week(double p) => Forecast(UsageWindowKinds.SevenDay, p, WeekReset, runsOut);

        var alert = Assert.Single(run.Plan(T0, Usage(Weekly(62)), Week(62)));
        Assert.Equal(AlertKind.WeeklyRunway, alert.Kind);
        Assert.Equal("Weekly limit ahead", alert.Title);
        Assert.Equal("At this pace the weekly limit runs out about Thu 3:00 PM, before it resets Fri 10:00 AM. Now at 62%.", alert.Body);
        Assert.Empty(run.Plan(T0.AddHours(6), Usage(Weekly(66)), Week(66)));
        Assert.Equal(AlertKind.WeeklyRunway, Assert.Single(run.Plan(T0.AddHours(12), Usage(Weekly(70)), Week(70))).Kind); // next day
    }

    // ---------------------------------------------------------- SpendPace

    [Fact]
    public void A_monthly_projection_past_the_cap_warns_at_most_once_a_day()
    {
        var run = new Run();
        var reset = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var month = new UsageWindow(UsageWindowKinds.MonthSpend, 60, null, null);
        var usage = Usage("claude", "Claude", new SpendCap(120m, 200m, 60, null, "month"), month);
        var forecast = Forecast(UsageWindowKinds.MonthSpend, 60, reset, null, projected: 106);

        var alert = Assert.Single(run.Plan(T0, usage, forecast));
        Assert.Equal(AlertKind.SpendPace, alert.Kind);
        Assert.Equal("On pace to pass your monthly cap", alert.Title);
        Assert.Equal("On pace for $212.00 by Nov 30 (cap $200.00).", alert.Body);
        Assert.Empty(run.Plan(T0.AddHours(5), usage, forecast));
        Assert.Single(run.Plan(T0.AddDays(1), usage, forecast));
    }

    [Fact]
    public void A_percent_only_spend_limit_projects_in_percent()
    {
        var run = new Run();
        var reset = T0.AddDays(10);
        var limit = new UsageWindow(UsageWindowKinds.SpendLimit, 50, reset, null);
        var usage = Usage("claude", "Claude", new SpendCap(null, null, 50, reset, null), limit);
        var alert = Assert.Single(run.Plan(T0, usage, Forecast(UsageWindowKinds.SpendLimit, 50, reset, null, projected: 130)));
        Assert.Equal("On pace for 130% of your spend limit by Nov 28.", alert.Body);
    }

    // ---------------------------------------------------------- silence and switches

    [Fact]
    public void Quiet_hours_across_midnight_drop_alerts()
    {
        var quiet = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0));
        var run = new Run(AlertSettings.Default with { Quiet = quiet });
        var night = new DateTimeOffset(2026, 11, 18, 23, 30, 0, TimeSpan.Zero);
        var reset = night.AddHours(10);

        Assert.Empty(run.Plan(night, Usage(FiveHour(85, reset))));
        Assert.Single(run.Dropped);
        // Dropped means told: the morning doesn't bring it back.
        Assert.Empty(run.Plan(night.AddHours(8), Usage(FiveHour(85, reset))));
        Assert.Empty(run.Dropped);

        var evening = new Run(AlertSettings.Default with { Quiet = quiet });
        Assert.Single(evening.Plan(new DateTimeOffset(2026, 11, 18, 21, 59, 0, TimeSpan.Zero), Usage(FiveHour(85, reset))));
        var morning = new Run(AlertSettings.Default with { Quiet = quiet });
        Assert.Single(morning.Plan(new DateTimeOffset(2026, 11, 19, 7, 0, 0, TimeSpan.Zero), Usage(FiveHour(85, night.AddHours(9)))));
    }

    [Fact]
    public void Muted_drops_alerts_until_the_mute_ends()
    {
        var run = new Run(AlertSettings.Default with { MutedUntil = T0.AddHours(1) });
        Assert.Empty(run.Plan(T0, Usage(FiveHour(85))));
        Assert.Single(run.Dropped);
        Assert.Single(run.Plan(T0.AddMinutes(61), Usage(FiveHour(96))));
    }

    [Fact]
    public void Alerts_off_plan_nothing_and_turned_on_they_start_from_now()
    {
        var run = new Run(AlertSettings.Default with { Enabled = false });
        Assert.Empty(run.Plan(T0, Usage(FiveHour(85))));
        Assert.Empty(run.Dropped);
        Assert.Empty(run.State.Told);

        run.Settings = AlertSettings.Default;
        Assert.Single(run.Plan(T0.AddMinutes(1), Usage(FiveHour(85))));
    }

    [Fact]
    public void Each_kind_has_its_own_switch()
    {
        Assert.Empty(new Run(AlertSettings.Default with { OnThreshold = false }).Plan(T0, Usage(FiveHour(85))));
        Assert.Empty(new Run(AlertSettings.Default with { OnForecast = false }).Plan(T0, Usage(FiveHour(70)),
            Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, T0.AddMinutes(20))));

        var noReset = new Run(AlertSettings.Default with { OnReset = false });
        noReset.Plan(T0, Usage(FiveHour(90)));
        Assert.Empty(noReset.Plan(FiveHourReset.AddMinutes(2), Usage(FiveHour(90))));
    }

    [Fact]
    public void With_two_providers_the_title_names_the_provider()
    {
        var run = new Run();
        var alerts = run.Plan(T0,
        [
            new AlertInput(Usage("claude", "Claude", null, FiveHour(85)), []),
            new AlertInput(Usage("codex", "Codex", null, FiveHour(90)), [])
        ]);
        Assert.Equal(["Claude: 5-hour window at 85%", "Codex: 5-hour window at 90%"], alerts.Select(a => a.Title));
        Assert.Empty(run.Plan(T0.AddMinutes(1),
        [
            new AlertInput(Usage("claude", "Claude", null, FiveHour(86)), []),
            new AlertInput(Usage("codex", "Codex", null, FiveHour(91)), [])
        ]));
    }

    // ---------------------------------------------------------- what is kept

    [Fact]
    public void Told_alerts_are_forgotten_a_day_after_their_reset()
    {
        var run = new Run();
        run.Plan(T0, Usage(FiveHour(85)));
        Assert.Single(run.State.Told);
        run.Plan(FiveHourReset.AddDays(1).AddMinutes(1), Usage());
        Assert.Empty(run.State.Told);
        Assert.Empty(run.State.Peaks);
    }

    [Fact]
    public void A_window_without_a_reset_is_told_again_once_it_has_started_over()
    {
        var run = new Run();
        UsageWindow Opus(double p) => new(UsageWindowKinds.SevenDayOpus, p, null, TimeSpan.FromDays(7));
        Assert.Equal("Claude doesn't say when it resets.", Assert.Single(run.Plan(T0, Usage(Opus(85)))).Body);
        Assert.Empty(run.Plan(T0.AddHours(1), Usage(Opus(80))));
        Assert.Empty(run.Plan(T0.AddDays(1), Usage(Opus(5)))); // started over
        Assert.Single(run.Plan(T0.AddDays(2), Usage(Opus(85))));
    }

    [Fact]
    public void A_per_model_weekly_window_follows_the_weekly_reset()
    {
        var run = new Run();
        var opus = new UsageWindow(UsageWindowKinds.SevenDayOpus, 85, null, TimeSpan.FromDays(7));
        var alert = Assert.Single(run.Plan(T0, Usage(Weekly(40), opus)));
        Assert.Equal("Weekly Opus window at 85%", alert.Title);
        Assert.Equal("Resets Fri 10:00 AM.", alert.Body);
        Assert.Contains("|seven_day_opus|2026-11-20T10:00Z|", alert.Key);
    }

    // ---------------------------------------------------------- the persisted state

    [Fact]
    public void A_new_instance_with_the_saved_state_does_not_repeat_an_alert()
    {
        using var dir = new TempDir();
        var path = dir.Combine("cache", AlertStateFile.FileName);
        var now = T0;
        var first = new UsageAlerts(path, () => now, Zone, EnUs);
        Assert.Single(first.Update([new AlertInput(Usage(FiveHour(90)), [])], AlertSettings.Default));
        Assert.True(File.Exists(path));

        now = T0.AddMinutes(5);
        var restarted = new UsageAlerts(path, () => now, Zone, EnUs);
        Assert.Empty(restarted.Update([new AlertInput(Usage(FiveHour(91)), [])], AlertSettings.Default));
        // The peak came through the restart too: the reset still says "you're back".
        now = FiveHourReset.AddSeconds(61);
        Assert.Equal(AlertKind.WindowReset,
            Assert.Single(restarted.Update([new AlertInput(Usage(FiveHour(91)), [])], AlertSettings.Default)).Kind);
    }

    [Fact]
    public void The_state_file_is_written_only_when_something_changed()
    {
        using var dir = new TempDir();
        var path = dir.Combine("cache", AlertStateFile.FileName);
        var alerts = new UsageAlerts(path, () => T0, Zone, EnUs);
        alerts.Update([new AlertInput(Usage(), [])], AlertSettings.Default);
        Assert.False(File.Exists(path));

        alerts.Update([new AlertInput(Usage(FiveHour(90)), [])], AlertSettings.Default);
        var written = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, written.AddMinutes(-5));
        alerts.Update([new AlertInput(Usage(FiveHour(90)), [])], AlertSettings.Default);
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Schema\":2,\"Told\":[]}")]
    [InlineData("null")]
    public void A_broken_or_newer_state_file_reads_as_empty(string content)
    {
        using var dir = new TempDir();
        var path = dir.Combine(AlertStateFile.FileName);
        File.WriteAllText(path, content);
        Assert.Same(AlertState.Empty, AlertStateFile.Load(path));
    }

    [Fact]
    public void The_state_round_trips_through_the_file()
    {
        var run = new Run();
        run.Plan(T0, Usage(FiveHour(70)), Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, T0.AddMinutes(20)));
        run.Plan(T0.AddMinutes(1), Usage(FiveHour(70)), Forecast(UsageWindowKinds.FiveHour, 70, FiveHourReset, null));
        Assert.NotNull(run.State.Told.Single().ClearSince);

        using var dir = new TempDir();
        var path = dir.Combine(AlertStateFile.FileName);
        File.WriteAllText(path, AlertStateFile.Serialize(run.State));
        var loaded = AlertStateFile.Load(path);
        Assert.Equal(run.State.Told, loaded.Told);
        Assert.Equal(run.State.Peaks, loaded.Peaks);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("Schema").GetInt32());
        Assert.Equal("ForecastHit", json.RootElement.GetProperty("Told")[0].GetProperty("AlertKind").GetString());
    }

    [Fact]
    public void The_test_alert_is_not_a_usage_alert()
    {
        var alert = AlertPlanner.TestAlert();
        Assert.Equal(AlertKind.Test, alert.Kind);
        Assert.Equal("Test alert", alert.Title);
    }
}
