using StickyAIusage.Alerts;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Codex beside Claude: its flyout section, the pill's chip, its pace and
/// alerts, and the Settings switch.</summary>
public class OtherProvidersTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 5, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private static ProviderUsage Codex(double fiveHour, double weekly = 20, UsageHealth health = UsageHealth.Ok,
        DateTimeOffset? observed = null, DateTimeOffset? fiveHourReset = null) =>
        new("codex", "Codex", "Plus",
            [
                new UsageWindow(UsageWindowKinds.FiveHour, fiveHour, fiveHourReset ?? Now.AddHours(2), TimeSpan.FromHours(5)),
                new UsageWindow(UsageWindowKinds.SevenDay, weekly, Now.AddDays(3), TimeSpan.FromDays(7))
            ],
            null, observed ?? Now.AddMinutes(-1), CodexUsageProvider.SourceIdValue, SourceTrust.Derived, health,
            health == UsageHealth.Stale ? CodexUsageProvider.StaleDetail : null)
        {
            WholePointsOnly = true,
            SourceLabel = CodexUsageProvider.SourceLabelText,
            WindowLabels = new Dictionary<string, string> { [UsageWindowKinds.FiveHour] = "5-hour window", [UsageWindowKinds.SevenDay] = "Weekly" }
        };

    private static UsageSnapshot ClaudePlan(double fiveHour, double weekly = 10) => new()
    {
        Mode = UsageMode.PlanLimits,
        FiveHourUtilizationPercent = fiveHour,
        FiveHourResetsAt = Now.AddHours(1),
        WeeklyUtilizationPercent = weekly,
        WeeklyResetsAt = Now.AddDays(4),
        FetchedAt = Now
    };

    private static UsageSnapshot ClaudeMonthly(double percent) => new()
    {
        Mode = UsageMode.MonthlySpend,
        HasMonthlySpend = true,
        MonthlySpendUsedUsd = (decimal)percent,
        MonthlySpendLimitUsd = 100m,
        MonthlySpendPercent = percent,
        FetchedAt = Now
    };

    private static WidgetViewModel View(UsageSnapshot claude, params ProviderUsage[] others) =>
        View(claude, new AppSettings(), others);

    private static WidgetViewModel View(UsageSnapshot claude, AppSettings settings, params ProviderUsage[] others) =>
        WidgetViewModel.From(claude, null, settings, Now, TimeZoneInfo.Utc,
            others: others.Select(o => new ProviderReading(o, [])).ToList());

    [Fact]
    public void Codex_gets_a_section_with_its_plan_time_source_and_rows()
    {
        var section = Assert.Single(View(ClaudePlan(43), Codex(61, 22)).ProviderSections);

        Assert.Equal("codex", section.ProviderId);
        Assert.Equal("Codex · Plus", section.Title);
        Assert.Equal("Updated 1m ago", section.UpdatedText);
        Assert.Equal("from Codex's local session log", section.SourceLabel);
        Assert.Null(section.Message);
        Assert.Equal(["5-hour window", "Weekly"], section.Rows.Select(r => r.Label));
        Assert.Equal("61% · Resets in 2h", section.Rows[0].Value);
        Assert.Equal(61, section.Rows[0].Percent);
        Assert.Equal(UsageLevel.Yellow, section.Rows[0].Level);
        Assert.StartsWith("22% · Resets ", section.Rows[1].Value);
    }

    [Fact]
    public void A_stale_reading_says_as_of_and_why()
    {
        var section = View(ClaudePlan(43), Codex(61, health: UsageHealth.Stale, observed: Now.AddMinutes(-40))).ProviderSections[0];

        Assert.Equal("As of 40m ago", section.UpdatedText);
        Assert.Equal("Updates when Codex runs.", section.Message);
        Assert.Equal(2, section.Rows.Count);
    }

    [Fact]
    public void A_tool_not_used_for_two_weeks_has_no_card_and_no_chip_until_it_runs_again()
    {
        // Seen on a Mac: a Codex card "As of 290d 17h ago" with two passed resets.
        var dormant = Codex(95, health: UsageHealth.Stale, observed: Now.AddDays(-290), fiveHourReset: Now.AddDays(1));
        var vm = View(ClaudePlan(43), dormant);
        Assert.Empty(vm.ProviderSections);
        Assert.Empty(vm.ExtraChips);

        // Thirteen days: still shown, with the age in days.
        var recent = View(ClaudePlan(43), Codex(61, health: UsageHealth.Stale, observed: Now.AddDays(-13))).ProviderSections;
        Assert.Equal("As of 13 days ago", Assert.Single(recent).UpdatedText);
        Assert.True(OtherProviders.IsDormant(dormant, Now));
    }

    [Fact]
    public void A_codex_mode_without_limits_says_so_and_has_no_rows()
    {
        var none = new ProviderUsage("codex", "Codex", null, [], null, Now, CodexUsageProvider.SourceIdValue,
            SourceTrust.Derived, UsageHealth.Unavailable, CodexUsageProvider.NoLimitsDetail)
        {
            SourceLabel = CodexUsageProvider.SourceLabelText
        };

        var vm = View(ClaudePlan(43), none);

        var section = Assert.Single(vm.ProviderSections);
        Assert.Equal("Codex", section.Title);
        Assert.Null(section.UpdatedText);
        Assert.Empty(section.Rows);
        Assert.Equal("Codex does not report limits in this mode.", section.Message);
        Assert.Empty(vm.ExtraChips);
    }

    [Fact]
    public void A_window_whose_reset_passed_reads_zero_and_reset_passed()
    {
        var section = View(ClaudePlan(43), Codex(97, fiveHourReset: Now.AddMinutes(-1))).ProviderSections[0];

        Assert.Equal("0% · Reset passed", section.Rows[0].Value);
        Assert.Equal(0, section.Rows[0].Percent);
    }

    [Fact]
    public void Without_other_tools_there_is_no_section_and_no_chip()
    {
        var vm = View(ClaudePlan(43));

        Assert.Empty(vm.ProviderSections);
        Assert.Empty(vm.ExtraChips);
    }

    [Fact]
    public void The_chip_appears_when_codex_is_past_orange_and_closer_to_its_limit_than_claude()
    {
        var vm = View(ClaudePlan(43), Codex(91, 30));

        var chip = Assert.Single(vm.ExtraChips);
        Assert.Equal("Codex 91%", chip.Text);
        Assert.Equal(UsageLevel.Orange, chip.Level);
        Assert.Equal("Codex 5-hour window: 91% · Resets in 2h", chip.Tooltip);
    }

    [Theory]
    [InlineData(79, 43, false)] // below orange (80)
    [InlineData(80, 43, true)] // at orange
    [InlineData(91, 95, false)] // Claude is closer
    [InlineData(91, 91, false)] // a tie: Claude keeps the pill
    [InlineData(96, 10, true)]
    public void The_chip_rule(double codex, double claude, bool chip)
    {
        Assert.Equal(chip, View(ClaudePlan(claude), Codex(codex)).ExtraChips.Count == 1);
    }

    [Fact]
    public void The_weekly_window_counts_for_the_chip_too_and_red_is_red()
    {
        var chip = Assert.Single(View(ClaudePlan(43), Codex(10, 97)).ExtraChips);

        Assert.Equal("Codex 97%", chip.Text);
        Assert.Equal(UsageLevel.Red, chip.Level);
        Assert.StartsWith("Codex weekly: 97%", chip.Tooltip);
    }

    [Fact]
    public void A_passed_reset_does_not_raise_a_chip()
    {
        Assert.Empty(View(ClaudePlan(43), Codex(97, fiveHourReset: Now.AddMinutes(-1))).ExtraChips);
    }

    [Fact]
    public void A_stale_reading_still_raises_the_chip_and_its_tooltip_says_as_of_when()
    {
        var chip = Assert.Single(View(ClaudePlan(43), Codex(91, health: UsageHealth.Stale, observed: Now.AddMinutes(-25))).ExtraChips);

        Assert.EndsWith("\nAs of 25m ago", chip.Tooltip);
    }

    [Fact]
    public void Every_claude_layout_carries_the_section_and_the_chip()
    {
        var unknown = new UsageSnapshot { Mode = UsageMode.Unknown, StatusDetail = ClaudeFeedUsageSource.WaitingDetail, FetchedAt = Now };
        foreach (var claude in new[] { ClaudePlan(43), ClaudeMonthly(48), unknown })
        {
            var vm = View(claude, Codex(91));
            Assert.Single(vm.ProviderSections);
            Assert.Equal("Codex 91%", Assert.Single(vm.ExtraChips).Text);
        }
        // A monthly cap closer to its limit than Codex keeps the pill to itself.
        Assert.Empty(View(ClaudeMonthly(95), Codex(91)).ExtraChips);
        // Without a Claude reading, Claude counts as 0%: below orange, no chip.
        Assert.Empty(View(unknown, Codex(70)).ExtraChips);
    }

    [Fact]
    public void With_usage_hidden_on_the_pill_there_is_no_chip_but_the_flyout_keeps_the_section()
    {
        var vm = View(ClaudePlan(43), new AppSettings { ShowMonthlySpend = false }, Codex(91));

        Assert.Empty(vm.ExtraChips);
        Assert.Single(vm.ProviderSections);
    }

    [Fact]
    public void Two_chips_come_fullest_first()
    {
        var other = Codex(85) with { ProviderId = "other", DisplayName = "Other" };

        var vm = View(ClaudePlan(10), other, Codex(93));

        Assert.Equal(["Codex 93%", "Other 85%"], vm.ExtraChips.Select(c => c.Text));
    }

    [Fact]
    public void The_pace_line_shows_under_a_codex_row()
    {
        var forecast = new PaceForecast(UsageWindowKinds.FiveHour, 61, 30, Now.AddMinutes(78), 120, 10, ForecastConfidence.High,
            "the last 30 minutes") { ResetsAt = Now.AddHours(2), IsActive = true };
        var vm = WidgetViewModel.From(ClaudePlan(43), null, new AppSettings(), Now, TimeZoneInfo.Utc,
            others: [new ProviderReading(Codex(61), [forecast])]);

        Assert.Equal("At this pace: limit at 10:23 AM, 42m before the reset", vm.ProviderSections[0].Rows[0].Detail2);

        var off = WidgetViewModel.From(ClaudePlan(43), null, new AppSettings { ShowForecast = false }, Now, TimeZoneInfo.Utc,
            others: [new ProviderReading(Codex(61), [forecast])]);
        Assert.Null(off.ProviderSections[0].Rows[0].Detail2);
    }

    // ----- pace and alerts -----

    [Fact]
    public void Each_provider_keeps_its_own_forecasts_and_only_claude_s_go_to_the_plugin()
    {
        var now = Now;
        var bridge = _dir.Combine("feed", AppServices.ForecastFileName);
        var forecaster = new UsageForecaster(new UsageJournal(_dir.Combine("cache", "journal.jsonl"), () => now), bridge,
            () => now, TimeZoneInfo.Utc);
        IReadOnlyList<PaceForecast> claude = [], codex = [];
        for (var i = 0; i <= 6; i++)
        {
            claude = forecaster.Update(ClaudePlan(40 + 5 * i).ToProviderUsage() with { ObservedAt = now });
            codex = forecaster.Update(Codex(10 + 2 * i, observed: now));
            now = now.AddMinutes(5);
        }

        Assert.Equal(70, claude.Single(f => f.Kind == UsageWindowKinds.FiveHour).PercentNow);
        Assert.Equal(22, codex.Single(f => f.Kind == UsageWindowKinds.FiveHour).PercentNow);
        Assert.Equal(ForecastConfidence.High, codex.Single(f => f.Kind == UsageWindowKinds.FiveHour).Confidence);
        Assert.Contains("\"percentUsed\":70", File.ReadAllText(bridge));
        Assert.DoesNotContain("\"percentUsed\":22", File.ReadAllText(bridge));
    }

    [Fact]
    public void An_unchanged_reading_gets_its_own_provider_s_forecasts_back()
    {
        var now = Now;
        var forecaster = new UsageForecaster(new UsageJournal(_dir.Combine("cache", "journal.jsonl"), () => now), null,
            () => now, TimeZoneInfo.Utc);
        var claude = ClaudePlan(40).ToProviderUsage() with { ObservedAt = now };
        forecaster.Update(claude);
        forecaster.Update(Codex(12, observed: now));
        now = now.AddSeconds(20);

        // Nothing new within the minute: the cached forecasts, and Claude's, not Codex's.
        var again = forecaster.Update(claude);

        Assert.Equal(40, again.Single(f => f.Kind == UsageWindowKinds.FiveHour).PercentNow);
    }

    [Fact]
    public void A_codex_window_needs_two_points_of_rise_for_a_pace()
    {
        var now = Now;
        var forecaster = new UsageForecaster(new UsageJournal(_dir.Combine("cache", "journal.jsonl"), () => now), null,
            () => now, TimeZoneInfo.Utc);
        IReadOnlyList<PaceForecast> codex = [], claude = [];
        // One point in 30 minutes: a whole-point source may just have rounded up.
        foreach (var percent in new double[] { 10, 10, 10, 11, 11, 11, 11 })
        {
            codex = forecaster.Update(Codex(percent, observed: now));
            claude = forecaster.Update(Codex(percent, observed: now) with { ProviderId = "claude", WholePointsOnly = false });
            now = now.AddMinutes(5);
        }

        Assert.Null(codex.Single(f => f.Kind == UsageWindowKinds.FiveHour).RatePerHour);
        Assert.NotNull(claude.Single(f => f.Kind == UsageWindowKinds.FiveHour).RatePerHour);
    }

    private sealed class FixedProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public string ProviderId => "claude";
        public string DisplayName => "Claude";

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

        public Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot.ToProviderUsage());
    }

    private sealed class FakeOther(ProviderUsage usage, bool present = true) : IProviderUsageReader
    {
        public string ProviderId => usage.ProviderId;
        public string DisplayName => usage.DisplayName;
        public bool IsPresent => present;
        public int Reads { get; private set; }

        public Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(usage);
        }
    }

    private UsageFeed Feed(IProviderUsageReader other, AppSettings settings, UsageAlerts? alerts = null) =>
        new(new FixedProvider(ClaudePlan(43) with { FetchedAt = DateTimeOffset.UtcNow }),
            new ClaudeSessionLogSource(_dir.Combine("projects")), new DailySpendTracker(_dir.Combine("day-spend.json")),
            alerts: alerts, alertSettings: () => AlertSettings.From(settings),
            others: [other], providerEnabled: settings.IsProviderEnabled);

    [Fact]
    public async Task The_summary_carries_codex_when_it_is_present_and_turned_on()
    {
        var codex = new FakeOther(Codex(61));
        var settings = new AppSettings();

        var summary = await Feed(codex, settings).GetSummaryAsync();

        Assert.Equal(61, Assert.Single(summary.Others).Usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Fact]
    public async Task Codex_turned_off_in_settings_is_not_read()
    {
        var codex = new FakeOther(Codex(61));
        var settings = new AppSettings();
        settings.SetProviderEnabled("codex", false);

        var summary = await Feed(codex, settings).GetSummaryAsync();

        Assert.Empty(summary.Others);
        Assert.Equal(0, codex.Reads);
    }

    [Fact]
    public async Task Without_codex_on_the_machine_nothing_of_it_is_left()
    {
        var codex = new FakeOther(Codex(61), present: false);

        var summary = await Feed(codex, new AppSettings()).GetSummaryAsync();

        Assert.Empty(summary.Others);
        Assert.Equal(0, codex.Reads);
        Assert.Empty(WidgetViewModel.From(summary.Usage, null, new AppSettings(), Now, others: summary.Others).ProviderSections);
    }

    [Fact]
    public async Task Codex_s_alerts_name_the_tool()
    {
        var codex = new FakeOther(Codex(86, 20, observed: DateTimeOffset.UtcNow) with
        {
            Windows = [new UsageWindow(UsageWindowKinds.FiveHour, 86, DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromHours(5))]
        });
        var alerts = new UsageAlerts(_dir.Combine("cache", AlertStateFile.FileName), () => DateTimeOffset.UtcNow);

        var summary = await Feed(codex, new AppSettings(), alerts).GetSummaryAsync();

        var alert = Assert.Single(summary.Alerts);
        Assert.StartsWith("Codex: ", alert.Title);
        Assert.Contains("|codex|", "|" + alert.Key);
    }

    [Fact]
    public async Task Codex_without_limits_leaves_claude_s_alerts_unprefixed()
    {
        var none = new FakeOther(new ProviderUsage("codex", "Codex", null, [], null, Now, CodexUsageProvider.SourceIdValue,
            SourceTrust.Derived, UsageHealth.Unavailable, CodexUsageProvider.NoLimitsDetail));
        var alerts = new UsageAlerts(_dir.Combine("cache", AlertStateFile.FileName), () => DateTimeOffset.UtcNow);
        var feed = new UsageFeed(new FixedProvider(ClaudePlan(86) with
            {
                FetchedAt = DateTimeOffset.UtcNow, FiveHourResetsAt = DateTimeOffset.UtcNow.AddHours(1),
                WeeklyResetsAt = DateTimeOffset.UtcNow.AddDays(3)
            }),
            new ClaudeSessionLogSource(_dir.Combine("projects")), new DailySpendTracker(_dir.Combine("day-spend.json")),
            alerts: alerts, others: [none]);

        var summary = await feed.GetSummaryAsync();

        Assert.Equal("5-hour window at 86%", Assert.Single(summary.Alerts).Title);
    }

    [Fact]
    public void Alerts_name_codex_s_windows_as_codex_names_them()
    {
        var usage = new ProviderUsage("codex", "Codex", "Plus",
            [
                new UsageWindow("daily_window", 86, Now.AddHours(6), TimeSpan.FromDays(1)),
                new UsageWindow("codex_other:five_hour", 10, Now.AddHours(2), TimeSpan.FromHours(5))
            ], null, Now, CodexUsageProvider.SourceIdValue, SourceTrust.Derived, UsageHealth.Ok, null)
        {
            WindowLabels = new Dictionary<string, string>
            {
                ["daily_window"] = "Daily", ["codex_other:five_hour"] = "codex-other · 5-hour window"
            }
        };
        var claude = ClaudePlan(10).ToProviderUsage();

        var plan = AlertPlanner.Plan([new AlertInput(claude, []), new AlertInput(usage, [])], AlertSettings.Default,
            AlertState.Empty, Now, TimeZoneInfo.Utc);

        var alert = Assert.Single(plan.Alerts);
        Assert.Equal("Codex: Daily at 86%", alert.Title);
    }

    [Fact]
    public void A_screen_reader_hears_the_row_and_the_section_not_their_fields()
    {
        var section = View(ClaudePlan(43), Codex(61, 22)).ProviderSections[0];

        Assert.Equal("Codex · Plus", section.ToString());
        Assert.Equal("5-hour window: 61% · Resets in 2h", section.Rows[0].ToString());
        Assert.Equal("Label: Value. Pace", new SummaryRow("Label", "Value", null, UsageLevel.Green, "Pace").ToString());
    }

    // ----- settings -----

    [Fact]
    public void Providers_are_on_by_default_and_the_switch_round_trips()
    {
        var path = _dir.Combine("settings.json");
        var settings = new AppSettings();
        Assert.True(settings.IsProviderEnabled("codex"));

        settings.SetProviderEnabled("codex", false);
        settings.SetProviderEnabled("codex", false);
        Assert.Equal(["codex"], settings.DisabledProviders);
        SettingsService.Save(settings, path);
        var loaded = SettingsService.Load(path);
        Assert.False(loaded.IsProviderEnabled("CODEX"));

        loaded.SetProviderEnabled("codex", true);
        Assert.True(loaded.IsProviderEnabled("codex"));
        Assert.Empty(loaded.DisabledProviders);
    }

    [Fact]
    public void A_settings_file_from_before_codex_shows_codex()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, """{"SchemaVersion":4,"ClaudeSource":"feed","ShowForecast":true}""");

        Assert.True(SettingsService.Load(path).IsProviderEnabled("codex"));
    }
}
