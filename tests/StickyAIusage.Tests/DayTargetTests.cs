using System.Globalization;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;
using State = StickyAIusage.Services.DailySpendTracker.State;

namespace StickyAIusage.Tests;

public class DailySpendTrackerTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);
    private static decimal NoCode() => 0m;

    [Fact]
    public void First_reading_ever_counts_todays_code_sessions_as_already_spent()
    {
        var state = DailySpendTracker.Advance(null, Today, 96.75m, () => 3.25m);
        Assert.Equal(93.50m, state.Baseline);
        Assert.Equal(3.25m, state.SpentToday);
    }

    [Fact]
    public void Later_readings_count_everything_the_month_total_gains()
    {
        var state = DailySpendTracker.Advance(null, Today, 96.75m, NoCode);
        state = DailySpendTracker.Advance(state, Today, 100.25m, () => throw new InvalidOperationException("not needed"));
        Assert.Equal(3.50m, state.SpentToday);
    }

    [Fact]
    public void A_new_day_starts_from_yesterdays_last_reading()
    {
        var yesterday = new State(Today.AddDays(-1), 80m, 0m, 96.75m);
        var state = DailySpendTracker.Advance(yesterday, Today, 97.00m, () => 50m);
        Assert.Equal(96.75m, state.Baseline); // the code estimate isn't used
        Assert.Equal(0.25m, state.SpentToday);
    }

    [Fact]
    public void After_a_gap_of_days_it_falls_back_to_the_code_estimate()
    {
        var old = new State(Today.AddDays(-4), 10m, 0m, 20m);
        var state = DailySpendTracker.Advance(old, Today, 60m, () => 2m);
        Assert.Equal(2m, state.SpentToday);
    }

    [Fact]
    public void A_month_reset_keeps_what_today_had_already_spent()
    {
        var state = new State(Today, 190m, 0m, 195m);   // $5 today so far
        state = DailySpendTracker.Advance(state, Today, 0.40m, NoCode);
        Assert.Equal(5.40m, state.SpentToday);
        state = DailySpendTracker.Advance(state, Today, 1.00m, NoCode);
        Assert.Equal(6.00m, state.SpentToday);
    }

    [Fact]
    public void Never_below_zero()
    {
        var state = DailySpendTracker.Advance(null, Today, 5m, () => 50m);
        Assert.Equal(0m, state.Baseline);
        Assert.Equal(5m, state.SpentToday);
    }

    [Fact]
    public void State_survives_a_restart()
    {
        using var dir = new TempDir();
        var path = dir.Combine("day-spend.json");
        Assert.Equal(1m, new DailySpendTracker(path).Record(50m, Today, () => 1m));
        // A new instance (restart) keeps the day's starting point.
        Assert.Equal(3m, new DailySpendTracker(path).Record(52m, Today, () => 99m));
    }
}

public class DayTargetTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("10", 10)]
    [InlineData("$12.50", 12.5)]
    [InlineData(" 7 ", 7)]
    public void Valid_targets(string text, double expected)
    {
        var r = DayTarget.Validate(text, Inv);
        Assert.True(r.IsValid);
        Assert.Equal((decimal)expected, r.Usd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("ten")]
    [InlineData("1000000")]
    public void Invalid_targets(string text) => Assert.False(DayTarget.Validate(text, Inv).IsValid);

    [Fact]
    public void Only_rolling_limit_plans_turn_it_off()
    {
        Assert.True(DayTarget.AppliesTo(UsageMode.MonthlySpend));
        Assert.True(DayTarget.AppliesTo(UsageMode.Unknown));
        Assert.False(DayTarget.AppliesTo(UsageMode.PlanLimits));
    }

    private static UsageSnapshot Monthly => new()
    {
        Mode = UsageMode.MonthlySpend, HasMonthlySpend = true, MonthlySpendUsedUsd = 96.75m,
        MonthlySpendLimitUsd = 200m, MonthlySpendPercent = 48.4, FetchedAt = Now
    };

    [Fact]
    public void Pill_shows_todays_spend_percent_and_ring()
    {
        var settings = new AppSettings { DayTargetUsd = 10m };
        var vm = WidgetViewModel.From(Monthly, null, settings, Now, spentToday: 4.2m);

        Assert.True(vm.ShowDayTarget);
        Assert.Equal(("$4.20", "42%"), (vm.DayAmountText, vm.DayPercentText));
        Assert.True(vm.ShowDayRing);
        Assert.Equal(42, vm.DayRingPercent, 3);
        Assert.Equal(UsageLevel.Green, vm.DayLevel);
        Assert.Contains(vm.SummaryRows, r => r is { Label: "Today", Value: "$4.20 of $10.00 · 42%" });
    }

    [Fact]
    public void Over_target_reads_past_100_but_the_ring_stops_full_and_red()
    {
        var vm = WidgetViewModel.From(Monthly, null, new AppSettings { DayTargetUsd = 5m }, Now, spentToday: 7.5m);
        Assert.Equal("150%", vm.DayPercentText);
        Assert.Equal(100, vm.DayRingPercent);
        Assert.Equal(UsageLevel.Red, vm.DayLevel);
    }

    [Fact]
    public void Each_part_can_be_hidden_and_all_hidden_hides_the_group()
    {
        var onlyRing = new AppSettings { ShowDayTargetAmount = false, ShowDayTargetPercent = false };
        var vm = WidgetViewModel.From(Monthly, null, onlyRing, Now, spentToday: 4.2m);
        Assert.True(vm.ShowDayTarget);
        Assert.Equal((null, null, true), (vm.DayAmountText, vm.DayPercentText, vm.ShowDayRing));

        var none = new AppSettings { ShowDayTargetAmount = false, ShowDayTargetPercent = false, ShowDayTargetRing = false };
        Assert.False(WidgetViewModel.From(Monthly, null, none, Now, spentToday: 4.2m).ShowDayTarget);

        var off = new AppSettings { ShowDayTarget = false };
        var hidden = WidgetViewModel.From(Monthly, null, off, Now, spentToday: 4.2m);
        Assert.False(hidden.ShowDayTarget);
        Assert.DoesNotContain(hidden.SummaryRows, r => r.Label == "Today");
    }

    [Fact]
    public void Never_on_a_rolling_limit_plan()
    {
        var plan = new UsageSnapshot { Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 40, FetchedAt = Now };
        var vm = WidgetViewModel.From(plan, null, new AppSettings(), Now, spentToday: 4.2m);
        Assert.False(vm.ShowDayTarget);
        Assert.DoesNotContain(vm.SummaryRows, r => r.Label == "Today");
    }
}
