using System.Text;
using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

public class UsageModeDetectionTests
{
    private static UsageSnapshot Map(string json, string? subscription = null)
    {
        using var doc = JsonDocument.Parse(json);
        return ClaudeUsageProvider.MapToSnapshot(doc.RootElement, subscription);
    }

    // Monthly-cap (Enterprise) response shape: spend block,
    // extra_usage mirroring it, and null rolling windows.
    private const string Enterprise = """
        {"five_hour":null,"seven_day":null,
         "spend":{"used":{"amount_minor":9675,"exponent":2},"limit":{"amount_minor":20000,"exponent":2},"percent":48.4,"severity":"normal"},
         "extra_usage":{"is_enabled":true,"monthly_limit":20000,"used_credits":9675,"utilization":48.4}}
        """;

    [Fact]
    public void Spend_block_means_a_monthly_cap()
    {
        var s = Map(Enterprise, "enterprise");
        Assert.Equal(UsageMode.MonthlySpend, s.Mode);
        Assert.Equal((96.75m, 200m, 48.4), (s.MonthlySpendUsedUsd, s.MonthlySpendLimitUsd, s.MonthlySpendPercent));
        Assert.Null(s.FiveHourUtilizationPercent);
    }

    [Fact]
    public void Flat_rolling_windows_mean_a_plan_and_extra_usage_is_credits_not_a_cap()
    {
        var s = Map("""
            {"five_hour":{"utilization":34.0,"resets_at":"2026-09-19T15:10:00+00:00"},
             "seven_day":{"utilization":12.0,"resets_at":"2026-09-22T09:00:00+00:00"},
             "extra_usage":{"is_enabled":true,"monthly_limit":5000,"used_credits":1106,"utilization":22.1,"currency":"USD"}}
            """, "pro");

        Assert.Equal(UsageMode.PlanLimits, s.Mode);
        Assert.False(s.HasMonthlySpend);
        Assert.Equal((34d, 12d), (s.FiveHourUtilizationPercent, s.WeeklyUtilizationPercent));
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 15, 10, 0, TimeSpan.Zero), s.FiveHourResetsAt);
        Assert.Equal(38.94m, s.ExtraUsageRemaining);
        Assert.Equal("pro", s.SubscriptionType);
    }

    [Fact]
    public void Newer_limits_array_is_read_when_flat_buckets_are_missing()
    {
        var s = Map("""
            {"limits":[{"kind":"session","percent":61,"resets_at":"2026-09-19T15:10:00Z"},
                       {"kind":"weekly_all","percent":27},
                       {"kind":"weekly_scoped","percent":5,"scope":{"model":{"display_name":"Opus"}}}],
             "extra_usage":{"is_enabled":false,"monthly_limit":null,"used_credits":null}}
            """);

        Assert.Equal(UsageMode.PlanLimits, s.Mode);
        Assert.Equal((61d, 27d), (s.FiveHourUtilizationPercent, s.WeeklyUtilizationPercent));
        Assert.False(s.ExtraUsageEnabled);
        Assert.Null(s.ExtraUsageRemaining);
    }

    [Fact]
    public void Extra_usage_alone_is_still_read_as_a_monthly_cap()
    {
        var s = Map("""{"extra_usage":{"is_enabled":true,"monthly_limit":20000,"used_credits":5000,"utilization":25}}""");
        Assert.Equal(UsageMode.MonthlySpend, s.Mode);
        Assert.Equal((50m, 200m, 25d), (s.MonthlySpendUsedUsd, s.MonthlySpendLimitUsd, s.MonthlySpendPercent));
    }

    [Fact]
    public void Nothing_usable_is_unknown()
    {
        Assert.Equal(UsageMode.Unknown, Map("""{"five_hour":null,"seven_day":null}""").Mode);
    }
}

public class WidgetViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly AppSettings Settings = new();

    private static SessionInfo Session => new()
    {
        SessionId = "3c8e62c4-17a7-453e-8385-88147c5aaf07",
        TranscriptPath = "/x.jsonl",
        WorkingDirectory = "/work/acme-webapp",
        Model = "claude-sonnet-5",
        StartedAt = Now.AddMinutes(-7),
        LastActivityAt = Now.AddSeconds(-30),
        EstimatedCostUsd = 3.05m
    };

    [Fact]
    public void Monthly_cap_layout_matches_the_windows_widget()
    {
        var usage = new UsageSnapshot
        {
            Mode = UsageMode.MonthlySpend, HasMonthlySpend = true, MonthlySpendUsedUsd = 96.75m,
            MonthlySpendLimitUsd = 200m, MonthlySpendPercent = 48.4, FetchedAt = Now.AddSeconds(-20)
        };
        var vm = WidgetViewModel.From(usage, Session, Settings, Now);

        Assert.Equal(UsageMode.MonthlySpend, vm.Mode);
        Assert.Equal(("$96.75", "48%"), (vm.PrimaryText, vm.SecondaryText));
        Assert.Null(vm.CreditsText);
        Assert.Null(vm.StaleText);
        Assert.Equal("$3.05", vm.SessionText);
        Assert.True(vm.ShowSession);
        var row = Assert.Single(vm.SummaryRows);
        Assert.Equal(("This month", "$96.75 of $200.00 · 48%"), (row.Label, row.Value));
    }

    private static readonly TimeZoneInfo Ist = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");

    [Fact]
    public void Plan_layout_shows_windows_resets_credits_and_freshness_but_no_session_cost()
    {
        var usage = new UsageSnapshot
        {
            Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 43, FiveHourResetsAt = Now.AddMinutes(126),
            // Fri 2026-09-25 10:00 UTC = 3:30 PM IST, as Claude's Settings → Usage shows it.
            WeeklyUtilizationPercent = 82, WeeklyResetsAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            ExtraUsageEnabled = true, ExtraUsageLimit = 50m, ExtraUsageUsed = 11.06m, SubscriptionType = "pro",
            FetchedAt = Now.AddMinutes(-12), IsStale = true
        };
        var vm = WidgetViewModel.From(usage, Session, Settings, Now, Ist);

        Assert.Equal(UsageMode.PlanLimits, vm.Mode);
        // Pill: "5h: 43% - Resets: 2h 6m │ 1w: 82% │ Rem. Credits: $38.94 │ Updated: 12m ago";
        // compact (taskbar / menu bar): "5h: 43% - 2h 6m │ 1w: 82%".
        Assert.Equal(("5h: 43%", "- Resets: 2h 6m", "- 2h 6m"), (vm.PrimaryText, vm.SecondaryText, vm.SecondaryCompactText));
        Assert.Equal(("1w: 82%", "Rem. Credits: $38.94", "Updated: 12m ago"), (vm.WeeklyText, vm.CreditsText, vm.UpdatedText));
        Assert.False(vm.ShowSession); // plan users don't get the session cost on the pill
        Assert.Equal(82, vm.RingPercent);          // the fuller window drives the ring
        Assert.Equal(UsageLevel.Orange, vm.Level); // 82% is past the 80% orange threshold
        Assert.Equal("Plan Usage Limits: Pro", vm.SummaryTitle);
        Assert.Equal("Updated 12m ago", vm.SummaryUpdatedText);
        Assert.Equal(
            ["5-hour window: 43% · Resets in 2h 6m", "Weekly: 82% · Resets Fri 3:30 PM", "Remaining credits: $38.94"],
            vm.SummaryRows.Select(r => $"{r.Label}: {r.Value}"));
    }

    [Fact]
    public void Prepaid_balance_wins_over_the_monthly_credit_limit()
    {
        var usage = new UsageSnapshot
        {
            FiveHourUtilizationPercent = 5, FiveHourResetsAt = Now.AddHours(1), WeeklyUtilizationPercent = 1,
            ExtraUsageEnabled = true, ExtraUsageLimit = 40m, ExtraUsageUsed = 35.62m, CreditBalance = 9.32m, FetchedAt = Now
        };
        Assert.Equal("Rem. Credits: $9.32", WidgetViewModel.From(usage, null, Settings, Now).CreditsText);
    }

    [Theory]
    [InlineData(null)]  // endpoint reports no window yet
    [InlineData(-5)]    // the reset time passed and there's no newer reading yet
    public void An_ended_five_hour_window_starts_with_the_next_message(int? resetMinutesFromNow)
    {
        var usage = new UsageSnapshot
        {
            FiveHourUtilizationPercent = resetMinutesFromNow is null ? 0 : 91,
            FiveHourResetsAt = resetMinutesFromNow is { } m ? Now.AddMinutes(m) : null,
            WeeklyUtilizationPercent = 14, WeeklyResetsAt = Now.AddDays(2), FetchedAt = Now
        };
        var vm = WidgetViewModel.From(usage, null, Settings, Now);

        Assert.Equal(("5h: 0%", "- Starts with your next message"), (vm.PrimaryText, vm.SecondaryText));
        Assert.Equal("5-hour window: 0% · Starts with your next message", $"{vm.SummaryRows[0].Label}: {vm.SummaryRows[0].Value}");
    }

    [Fact]
    public void Plan_without_extra_usage_has_no_credits()
    {
        var usage = new UsageSnapshot { FiveHourUtilizationPercent = 5, FiveHourResetsAt = Now.AddHours(1), WeeklyUtilizationPercent = 1, ExtraUsageEnabled = false, FetchedAt = Now };
        var vm = WidgetViewModel.From(usage, null, Settings, Now);

        Assert.Null(vm.CreditsText);
        Assert.Equal(2, vm.SummaryRows.Count);
        Assert.Equal("Plan Usage Limits", vm.SummaryTitle);
    }

    [Fact]
    public void Monthly_layout_keeps_the_session_cost()
    {
        var usage = new UsageSnapshot { HasMonthlySpend = true, MonthlySpendUsedUsd = 1, MonthlySpendLimitUsd = 200, MonthlySpendPercent = 0.5, SubscriptionType = "enterprise", FetchedAt = Now };
        var vm = WidgetViewModel.From(usage, Session, Settings, Now);
        Assert.True(vm.ShowSession);
        Assert.Null(vm.WeeklyText);
        Assert.Equal("Plan Usage Limits: Enterprise", vm.SummaryTitle);
    }

    [Fact]
    public void Expired_sign_in_and_no_data_are_distinct()
    {
        var signIn = WidgetViewModel.From(new UsageSnapshot { RequiresReauth = true, IsStale = true }, null, Settings, Now);
        var none = WidgetViewModel.From(new UsageSnapshot { IsStale = true }, null, Settings, Now);

        Assert.Equal((UsageState.SignIn, "Sign in to Claude"), (signIn.State, signIn.PrimaryText));
        Assert.Equal((UsageState.Unavailable, "Usage unavailable"), (none.State, none.PrimaryText));
        Assert.False(none.ShowRing);
        Assert.Empty(none.SummaryRows);
        Assert.Null(none.StaleText);
    }

    [Theory]
    [InlineData(null, ThemePreference.System)]
    [InlineData("", ThemePreference.System)]
    [InlineData("system", ThemePreference.System)]
    [InlineData("Light", ThemePreference.Light)]
    [InlineData("DARK", ThemePreference.Dark)]
    [InlineData("nonsense", ThemePreference.System)]
    public void Theme_preference_round_trips_through_settings(string? saved, ThemePreference expected)
    {
        var preference = ThemePreferences.Parse(saved);
        Assert.Equal(expected, preference);
        Assert.Equal(preference, ThemePreferences.Parse(preference.ToSetting()));
    }

    [Fact]
    public void System_theme_follows_the_os_and_the_toggle_pins_the_other_one()
    {
        Assert.True(ThemePreferences.IsDark(ThemePreference.System, systemIsDark: true));
        Assert.False(ThemePreferences.IsDark(ThemePreference.System, systemIsDark: false));
        // A pinned theme ignores the OS.
        Assert.True(ThemePreferences.IsDark(ThemePreference.Dark, systemIsDark: false));
        Assert.False(ThemePreferences.IsDark(ThemePreference.Light, systemIsDark: true));
        // One click always flips what's on screen, even from "system".
        Assert.Equal(ThemePreference.Light, ThemePreferences.Toggled(isDarkNow: true));
        Assert.Equal(ThemePreference.Dark, ThemePreferences.Toggled(isDarkNow: false));
    }

    [Theory]
    [InlineData(12.5, null, "$12.50")]
    [InlineData(12.5, "EUR", "€12.50")]
    [InlineData(12.5, "cad", "12.50 CAD")]
    public void Money_uses_the_reported_currency(double amount, string? currency, string expected) =>
        Assert.Equal(expected, Format.Money((decimal)amount, currency));

    [Theory]
    [InlineData("2026-09-25T10:00:00Z", "Resets Fri 3:30 PM")]
    [InlineData("2026-09-19T11:50:00Z", "Resets today 5:20 PM")]
    [InlineData("2026-09-20T04:00:00Z", "Resets Sun 9:30 AM")]
    [InlineData("2026-09-28T10:00:00Z", "Resets Sep 28, 3:30 PM")]
    public void Weekly_reset_is_a_local_day_and_time(string resetUtc, string expected) =>
        Assert.Equal(expected, Format.ResetsAt(DateTimeOffset.Parse(resetUtc), Now, Ist));

    [Theory]
    [InlineData("StickyUsage", "Sticky Usage")]
    [InlineData("MyAIProject", "My AI Project")]
    [InlineData("acme-webapp", "acme-webapp")]
    [InlineData("webapp", "webapp")]
    public void Project_names_get_spaces_between_joined_words(string folder, string expected) =>
        Assert.Equal(expected, Format.ProjectName(folder));

    [Theory]
    [InlineData("high", "High")]
    [InlineData("xhigh", "Extra high")]
    [InlineData("max", "Max")]
    [InlineData("ultracode", "Ultracode")]
    [InlineData(null, "Default")]
    public void Effort_levels(string? level, string expected) => Assert.Equal(expected, Format.Effort(level));

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("max", "Max")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData(null, null)]
    public void Plan_names(string? subscription, string? expected) => Assert.Equal(expected, Format.PlanName(subscription));
}

public class MacKeychainCredentialStoreTests
{
    [Fact]
    public void Account_is_parsed_from_the_attribute_listing()
    {
        const string listing = """
            keychain: "/Users/me/Library/Keychains/login.keychain-db"
            class: "genp"
            attributes:
                "acct"<blob>="jdoe"
                "svce"<blob>="Claude Code-credentials"
            """;
        Assert.Equal("jdoe", MacKeychainCredentialStore.ParseAccount(listing));
        Assert.Null(MacKeychainCredentialStore.ParseAccount("nothing here"));
    }

    [Fact]
    public void Update_command_quotes_names_and_hex_encodes_the_secret()
    {
        var json = """{"claudeAiOauth":{"accessToken":"a b\"c"}}""";
        var command = MacKeychainCredentialStore.BuildUpdateCommand("j doe", "Claude Code-credentials", json);

        Assert.StartsWith("add-generic-password -U -a \"j doe\" -s \"Claude Code-credentials\" -X ", command);
        var hex = command[(command.LastIndexOf(' ') + 1)..];
        Assert.Equal(json, Encoding.UTF8.GetString(Convert.FromHexString(hex)));
    }
}

/// <summary>Round-trips a throwaway item through the real login Keychain.
/// Opt-in (STICKYAIUSAGE_KEYCHAIN_TEST=1, macOS only) because it touches the
/// developer's Keychain; it never reads or writes Claude's own item.</summary>
public class MacKeychainRoundTripTests
{
    [Fact]
    public void Write_updates_an_existing_item_in_place()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("STICKYAIUSAGE_KEYCHAIN_TEST") != "1")
            return;

        var service = "StickyAIusage-selftest-" + Guid.NewGuid().ToString("N")[..8];
        Security("add-generic-password", "-a", Environment.UserName, "-s", service, "-w", "{\"v\":1}");
        try
        {
            var store = new MacKeychainCredentialStore(service);
            Assert.Equal("{\"v\":1}", store.Read());

            Assert.True(store.Write("{\"claudeAiOauth\":{\"accessToken\":\"x y\"}}"));
            Assert.Equal("{\"claudeAiOauth\":{\"accessToken\":\"x y\"}}", store.Read());

            // Updated in place, not added as a second item.
            Assert.Equal(0, Security("delete-generic-password", "-s", service));
            Assert.Null(store.Read());
        }
        finally
        {
            Security("delete-generic-password", "-s", service);
        }
    }

    private static int Security(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/security")
            { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

public class ResetDrivenFetchTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_reset_passing_since_the_last_fetch_makes_a_fetch_due()
    {
        var snapshot = new UsageSnapshot { FiveHourResetsAt = T, WeeklyResetsAt = T.AddDays(3) };

        Assert.False(ClaudeUsageProvider.ResetPassedSinceLastFetch(snapshot, T.AddMinutes(-40), T.AddSeconds(5)));  // grace period
        Assert.True(ClaudeUsageProvider.ResetPassedSinceLastFetch(snapshot, T.AddMinutes(-40), T.AddMinutes(1)));
        Assert.False(ClaudeUsageProvider.ResetPassedSinceLastFetch(snapshot, T.AddMinutes(1), T.AddMinutes(2)));    // already refetched
        Assert.False(ClaudeUsageProvider.ResetPassedSinceLastFetch(null, T, T.AddHours(1)));
    }

    [Fact]
    public void Spend_balance_is_read_as_the_prepaid_credit_balance()
    {
        // Pro response shape, with a balance filled in.
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"five_hour":{"utilization":52.0,"resets_at":"2026-09-19T11:50:00.224629+00:00"},
             "seven_day":{"utilization":15.0,"resets_at":"2026-09-25T10:00:00.224654+00:00"},
             "extra_usage":{"is_enabled":false,"monthly_limit":null,"used_credits":null,"currency":null},
             "spend":{"used":{"amount_minor":0,"currency":"USD","exponent":2},"limit":null,"percent":0,"enabled":false,
                      "balance":{"amount_minor":932,"currency":"USD","exponent":2}}}
            """);
        var s = ClaudeUsageProvider.MapToSnapshot(doc.RootElement, "pro");

        Assert.Equal(UsageMode.PlanLimits, s.Mode); // spend with no limit isn't a monthly cap
        Assert.Equal(9.32m, s.CreditsRemaining);
        Assert.Equal("USD", s.Currency);
    }
}

public class FlyoutPlacementTests
{
    // A 1512x982 screen with a 33pt menu bar (macOS), and a 1920x1080 one with
    // a 48px taskbar (Windows).
    private static readonly Box MacScreen = new(0, 0, 1512, 982), MacWork = new(0, 33, 1512, 949);
    private static readonly Box WinScreen = new(0, 0, 1920, 1080), WinWork = new(0, 0, 1920, 1032);

    [Fact]
    public void Opens_below_a_pill_in_the_menu_bar_and_above_one_on_the_taskbar()
    {
        var mac = FlyoutPlacement.Place(new Box(850, 3, 180, 28), 500, 600, MacScreen, MacWork, preferAbove: false, gap: 8);
        Assert.False(mac.OpensAbove);
        Assert.Equal(new Box(530, 39, 500, 600), mac.Flyout);

        var win = FlyoutPlacement.Place(new Box(1500, 1040, 300, 32), 540, 600, WinScreen, WinWork, preferAbove: true, gap: 8);
        Assert.True(win.OpensAbove);
        Assert.Equal(1032, win.Flyout.Bottom);
    }

    [Fact]
    public void Flips_when_the_natural_side_has_no_room()
    {
        // Windows pill dragged to the top of the screen: opens below instead.
        var win = FlyoutPlacement.Place(new Box(900, 10, 300, 32), 540, 600, WinScreen, WinWork, preferAbove: true, gap: 8);
        Assert.False(win.OpensAbove);
        Assert.True(win.Flyout.Bottom <= WinWork.Bottom);

        // Mac pill dragged near the bottom: opens above instead.
        var mac = FlyoutPlacement.Place(new Box(700, 900, 180, 28), 500, 600, MacScreen, MacWork, preferAbove: false, gap: 8);
        Assert.True(mac.OpensAbove);
        Assert.True(mac.Flyout.Y >= MacWork.Y);
    }

    [Fact]
    public void Everything_stays_on_screen_when_the_pill_hangs_off_an_edge()
    {
        var right = FlyoutPlacement.Place(new Box(1400, 300, 400, 30), 500, 400, MacScreen, MacWork, false, 8);
        Assert.Equal(1512, right.Pill.Right);
        Assert.Equal(1512, right.Flyout.Right);

        var left = FlyoutPlacement.Place(new Box(-250, 300, 400, 30), 500, 400, MacScreen, MacWork, false, 8);
        Assert.Equal(0, left.Pill.X);
        Assert.Equal(0, left.Flyout.X);
    }

    [Fact]
    public void A_flyout_too_tall_for_either_side_takes_the_roomier_one_and_scrolls()
    {
        var layout = FlyoutPlacement.Place(new Box(700, 600, 180, 28), 500, 900, MacScreen, MacWork, false, 8);
        Assert.True(layout.OpensAbove); // 559 above vs 346 below
        Assert.Equal(559, layout.MaxFlyoutHeight);
        Assert.Equal(MacWork.Y, layout.Flyout.Y);
    }

    [Fact]
    public void Growing_content_keeps_the_side_it_opened_on()
    {
        var first = FlyoutPlacement.Place(new Box(700, 400, 180, 28), 500, 200, MacScreen, MacWork, false, 8);
        var grown = FlyoutPlacement.Place(new Box(700, 400, 180, 28), 500, 900, MacScreen, MacWork, false, 8, forceAbove: first.OpensAbove);
        Assert.False(grown.OpensAbove);
        Assert.Equal(MacWork.Bottom, grown.Flyout.Bottom);
    }
}

public class FlyoutHeightCapTests
{
    [Fact]
    public void A_cap_keeps_the_flyout_off_the_full_height_of_a_small_screen()
    {
        var screen = new Box(0, 0, 1366, 768);
        var work = new Box(0, 0, 1366, 720);
        var layout = FlyoutPlacement.Place(new Box(1000, 680, 300, 32), 540, 700, screen, work,
            preferAbove: true, gap: 8, maxHeightCap: 768 * 0.75);
        Assert.Equal(576, layout.Flyout.Height, 1);
        Assert.Equal(576, layout.MaxFlyoutHeight, 1);
    }

    [Fact]
    public void Repeated_rate_limits_back_off_longer_up_to_a_cap()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), ClaudeUsageProvider.RateLimitBackoff(null, 1));
        Assert.Equal(TimeSpan.FromMinutes(8), ClaudeUsageProvider.RateLimitBackoff(null, 3));
        Assert.Equal(TimeSpan.FromMinutes(30), ClaudeUsageProvider.RateLimitBackoff(null, 9));
        Assert.Equal(TimeSpan.FromMinutes(30), ClaudeUsageProvider.RateLimitBackoff(TimeSpan.FromDays(3), 1));
    }
}
