using System.Globalization;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>How a forecast reads in the pill and the flyout, in all three layouts.</summary>
public class ForecastViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero); // a Tuesday
    private static readonly DateTimeOffset FiveHourReset = Now.AddHours(2);
    private static readonly DateTimeOffset WeekReset = Now.AddDays(3);
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    private static UsageSnapshot Plan(double fiveHour = 70, double weekly = 40) => new()
    {
        FiveHourUtilizationPercent = fiveHour,
        FiveHourResetsAt = FiveHourReset,
        WeeklyUtilizationPercent = weekly,
        WeeklyResetsAt = WeekReset,
        FetchedAt = Now
    };

    private static PaceForecast FiveHour(double? hitsInMinutes, ForecastConfidence confidence = ForecastConfidence.Low,
        double? projected = null, double percent = 70) =>
        new(UsageWindowKinds.FiveHour, percent, 60, hitsInMinutes is { } m ? Now.AddMinutes(m) : null, projected, 10,
            confidence, "the last 30 minutes") { ResetsAt = FiveHourReset, IsActive = true };

    private static PaceForecast Weekly(double? hitsInHours, double ahead = 0, double? projected = null,
        ForecastConfidence confidence = ForecastConfidence.Low) =>
        new(UsageWindowKinds.SevenDay, 40, 1, hitsInHours is { } h ? Now.AddHours(h) : null, projected, ahead,
            confidence, "the last 24 hours") { ResetsAt = WeekReset, IsActive = true };

    private static PaceForecast Idle(string kind, DateTimeOffset reset) =>
        PaceForecast.None(kind, 40, reset, "the last 30 minutes");

    private static WidgetViewModel View(UsageSnapshot usage, AppSettings? settings = null, CultureInfo? culture = null,
        params PaceForecast[] forecasts) =>
        WidgetViewModel.From(usage, null, settings ?? new AppSettings(), Now, TimeZoneInfo.Utc, forecasts: forecasts,
            culture: culture ?? Us);

    [Fact]
    public void A_limit_before_the_reset_replaces_the_countdown_in_the_pill()
    {
        var vm = View(Plan(), forecasts: [FiveHour(20), Idle(UsageWindowKinds.SevenDay, WeekReset)]);

        Assert.Equal("5h: 70%", vm.PrimaryText);
        Assert.Null(vm.SecondaryText);
        Assert.Null(vm.SecondaryCompactText);
        Assert.Equal("- limit ~2:20 PM", vm.ForecastChipText);
        Assert.Equal("→ 2:20", vm.ForecastChipCompactText);
        Assert.Equal(UsageLevel.Red, vm.ForecastLevel);
        Assert.Contains("At this pace: 5-hour limit at about 2:20 PM, 1h 40m before the reset.", vm.UsageTooltip);
        Assert.Contains("Based on the last 30 minutes of activity. An estimate.", vm.UsageTooltip);
        Assert.Equal("At this pace: limit at 2:20 PM, 1h 40m before the reset", vm.SummaryRows[0].Detail2);
    }

    [Theory]
    [InlineData(5, UsageLevel.Red)]
    [InlineData(30, UsageLevel.Red)]
    [InlineData(31, UsageLevel.Orange)]
    [InlineData(60, UsageLevel.Orange)]
    [InlineData(61, UsageLevel.Yellow)]
    [InlineData(110, UsageLevel.Yellow)]
    public void The_chip_color_follows_how_soon_the_limit_comes(double minutes, UsageLevel expected) =>
        Assert.Equal(expected, View(Plan(), forecasts: FiveHour(minutes)).ForecastLevel);

    [Fact]
    public void Times_follow_a_24_hour_clock()
    {
        var vm = View(Plan(), culture: Uk, forecasts: [FiveHour(20), Weekly(24, ahead: 3)]);

        Assert.Equal("- limit ~14:20", vm.ForecastChipText);
        Assert.Equal("→ 14:20", vm.ForecastChipCompactText);
        Assert.Equal("At this pace: limit Wed 14:00, 2d before the reset · 3 points ahead of an even pace", vm.SummaryRows[1].Detail2);
        Assert.EndsWith("Resets Fri 14:00", vm.SummaryRows[1].Value);
    }

    [Fact]
    public void Turned_off_the_pill_and_flyout_look_as_before()
    {
        var off = View(Plan(), new AppSettings { ShowForecast = false }, forecasts: [FiveHour(20), Weekly(10, ahead: 8)]);
        var none = View(Plan());

        Assert.Equal(none.SecondaryText, off.SecondaryText);
        Assert.Equal("- Resets: 2h", off.SecondaryText);
        Assert.Equal("- 2h", off.SecondaryCompactText);
        Assert.Null(off.ForecastChipText);
        Assert.Null(off.ForecastChipCompactText);
        Assert.Null(off.WeeklyLevel);
        Assert.Null(off.ForecastTooltip);
        Assert.All(off.SummaryRows, r => Assert.Null(r.Detail2));
        Assert.Equal(none.UsageTooltip, off.UsageTooltip);
    }

    [Fact]
    public void Without_a_rate_there_is_no_forecast()
    {
        var vm = View(Plan(), forecasts: [Idle(UsageWindowKinds.FiveHour, FiveHourReset), Idle(UsageWindowKinds.SevenDay, WeekReset)]);

        Assert.Equal("- Resets: 2h", vm.SecondaryText);
        Assert.Null(vm.ForecastChipText);
        Assert.Null(vm.SummaryRows[0].Detail2);
        Assert.Null(vm.ForecastTooltip);
    }

    [Fact]
    public void A_high_confidence_floor_hides_low_confidence_forecasts()
    {
        var settings = new AppSettings { ForecastMinConfidence = "high" };

        Assert.Null(View(Plan(), settings, forecasts: FiveHour(20, ForecastConfidence.Low)).ForecastChipText);
        Assert.Equal("- limit ~2:20 PM", View(Plan(), settings, forecasts: FiveHour(20, ForecastConfidence.High)).ForecastChipText);
    }

    [Theory]
    [InlineData("low", ForecastConfidence.Low)]
    [InlineData("HIGH", ForecastConfidence.High)]
    [InlineData(" high ", ForecastConfidence.High)]
    [InlineData("medium", ForecastConfidence.Low)]
    [InlineData("", ForecastConfidence.Low)]
    [InlineData(null, ForecastConfidence.Low)]
    public void The_confidence_floor_setting_parses(string? value, ForecastConfidence expected) =>
        Assert.Equal(expected, new AppSettings { ForecastMinConfidence = value! }.ForecastFloor);

    [Fact]
    public void A_reached_limit_says_so_in_red()
    {
        var vm = View(Plan(fiveHour: 100), forecasts: FiveHour(null, percent: 100));

        Assert.Equal("- limit reached · resets 4:00 PM", vm.ForecastChipText);
        Assert.Equal("- 2h", vm.ForecastChipCompactText);
        Assert.Equal(UsageLevel.Red, vm.ForecastLevel);
        Assert.Null(vm.SecondaryText);
        Assert.Null(vm.SecondaryCompactText);
        // The row already says 100%: no "on pace to use it all" under it.
        Assert.Null(View(Plan(fiveHour: 100), forecasts: FiveHour(null, projected: 120, percent: 100)).SummaryRows[0].Detail2);
    }

    [Fact]
    public void A_weekly_limit_within_two_days_gets_the_chip_when_the_five_hour_window_has_none()
    {
        var vm = View(Plan(), forecasts: [Idle(UsageWindowKinds.FiveHour, FiveHourReset), Weekly(20)]);

        Assert.Equal("- weekly limit ~Wed 10:00 AM", vm.ForecastChipText);
        Assert.Null(vm.SecondaryText);
        // Compact: the countdown stays; the weekly text takes the color.
        Assert.Null(vm.ForecastChipCompactText);
        Assert.Equal("- 2h", vm.SecondaryCompactText);
        Assert.Equal(UsageLevel.Orange, vm.WeeklyLevel);
        Assert.Equal(UsageLevel.Orange, vm.ForecastLevel);
        Assert.Contains("At this pace: weekly limit Wed 10:00 AM, 2d 4h before the reset.", vm.UsageTooltip);
        Assert.Contains("Based on the last 24 hours of activity. An estimate.", vm.UsageTooltip);
    }

    [Fact]
    public void The_five_hour_chip_wins_and_the_weekly_still_takes_its_color()
    {
        var vm = View(Plan(), forecasts: [FiveHour(45), Weekly(5)]);

        Assert.Equal("- limit ~2:45 PM", vm.ForecastChipText);
        Assert.Equal(UsageLevel.Red, vm.WeeklyLevel);
        Assert.Contains("Based on the last 30 minutes and the last 24 hours of activity.", vm.UsageTooltip);
    }

    [Fact]
    public void A_weekly_limit_further_than_two_days_away_stays_in_the_flyout()
    {
        var vm = View(Plan(), forecasts: Weekly(60));

        Assert.Null(vm.ForecastChipText);
        Assert.Null(vm.WeeklyLevel);
        Assert.Equal("At this pace: limit Fri 2:00 AM, 12h before the reset · on an even pace", vm.SummaryRows[1].Detail2);
    }

    [Fact]
    public void A_used_up_weekly_window_turns_its_text_red()
    {
        var vm = View(Plan(weekly: 100), forecasts: [Idle(UsageWindowKinds.FiveHour, FiveHourReset), Weekly(null)]);

        Assert.Equal(UsageLevel.Red, vm.WeeklyLevel);
        Assert.Equal("- weekly limit reached", vm.ForecastChipText);
    }

    [Theory]
    [InlineData(61.4, 6.4, "On pace to end near 61% · 6 points ahead of an even pace")]
    [InlineData(130, -5, "On pace to use it all before the reset · under an even pace")]
    [InlineData(45, 1, "On pace to end near 45% · on an even pace")]
    public void The_flyout_says_where_the_window_ends(double projected, double ahead, string expected) =>
        Assert.Equal(expected, View(Plan(), forecasts: Weekly(null, ahead, projected)).SummaryRows[1].Detail2);

    [Fact]
    public void The_even_pace_needs_no_rate()
    {
        var idle = Idle(UsageWindowKinds.SevenDay, WeekReset) with { AheadOfEvenPace = -12 };

        Assert.Equal("under an even pace", View(Plan(), forecasts: idle).SummaryRows[1].Detail2);
    }

    [Fact]
    public void A_forecast_for_a_window_that_already_reset_is_ignored()
    {
        var old = FiveHour(20) with { ResetsAt = Now.AddMinutes(-1) };

        Assert.Null(View(Plan(), forecasts: old).ForecastChipText);
    }

    private static UsageSnapshot MonthlyCap(decimal used = 120) => new()
    {
        HasMonthlySpend = true,
        MonthlySpendUsedUsd = used,
        MonthlySpendLimitUsd = 200,
        MonthlySpendPercent = (double)(used / 2),
        FetchedAt = Now
    };

    private static PaceForecast Month(double projected, double ahead = 5) =>
        new(UsageWindowKinds.MonthSpend, 60, 0.1, null, projected, ahead, ForecastConfidence.High, "the last 7 days")
        {
            ResetsAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            IsActive = true
        };

    [Fact]
    public void A_monthly_cap_shows_where_the_month_ends_and_warns_past_the_cap()
    {
        var vm = View(MonthlyCap(), forecasts: Month(106));

        var row = vm.SummaryRows.Single(r => r.Label == "At this pace");
        Assert.Equal("$212.00 by Nov 30 (cap $200.00)", row.Value);
        Assert.Null(row.Percent);
        Assert.Equal("5 points ahead of an even pace", vm.SummaryRows[0].Detail2);
        Assert.Equal("→ $212.00", vm.ForecastChipText);
        Assert.Null(vm.ForecastChipCompactText); // the compact pill keeps its width
        Assert.Equal(UsageLevel.Orange, vm.ForecastLevel);
        Assert.Equal("60%", vm.SecondaryText); // the percentage stays beside the chip
        Assert.Contains("At this pace: $212.00 by Nov 30, over the $200.00 cap.", vm.UsageTooltip);
        Assert.Contains("Based on the last 7 days of activity.", vm.UsageTooltip);
    }

    [Fact]
    public void A_monthly_projection_under_the_cap_has_no_chip()
    {
        var vm = View(MonthlyCap(), forecasts: Month(90));

        Assert.Equal("$180.00 by Nov 30 (cap $200.00)", vm.SummaryRows.Single(r => r.Label == "At this pace").Value);
        Assert.Null(vm.ForecastChipText);
        Assert.Equal("60%", vm.SecondaryText);
    }

    [Fact]
    public void A_percent_only_spend_limit_projects_in_percent()
    {
        var usage = new UsageSnapshot
        {
            HasMonthlySpend = true, MonthlySpendPercentOnly = true, MonthlySpendPercent = 60,
            MonthlySpendResetsAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), FetchedAt = Now
        };
        var forecast = Month(120) with { Kind = UsageWindowKinds.SpendLimit };

        var vm = View(usage, forecasts: forecast);

        Assert.Equal("120% of your limit by Nov 30", vm.SummaryRows.Single(r => r.Label == "At this pace").Value);
        Assert.Equal("→ 120%", vm.ForecastChipText);
    }

    [Fact]
    public void The_unknown_layout_shows_no_forecast()
    {
        var vm = View(new UsageSnapshot { IsStale = true, FetchedAt = Now }, forecasts: FiveHour(20));

        Assert.Equal(UsageMode.Unknown, vm.Mode);
        Assert.Null(vm.ForecastChipText);
        Assert.Null(vm.ForecastTooltip);
    }

    [Fact]
    public void Other_windows_get_their_pace_line()
    {
        var usage = Plan() with
        {
            OtherWindows = [new UsageWindow("seven_day_cowork", 50, Now.AddDays(2), TimeSpan.FromDays(7))]
        };
        var forecast = new PaceForecast("seven_day_cowork", 50, 2, Now.AddHours(25), null, null, ForecastConfidence.Low,
            "the last 17 hours") { ResetsAt = Now.AddDays(2) };

        var vm = View(usage, forecasts: forecast);

        Assert.Equal("At this pace: limit Wed 3:00 PM, 23h before the reset",
            vm.SummaryRows.Single(r => r.Label == "Seven day cowork").Detail2);
    }
}
