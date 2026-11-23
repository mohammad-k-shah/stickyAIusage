using StickyAIusage.Health;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>Settings → Health: every item in every status from injected facts,
/// one concrete action wherever something isn't right, and a report without
/// the home folder.</summary>
public class HealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>A machine where everything works: Claude Code 2.1.325, the plugin connected a minute ago, live numbers.</summary>
    internal static HealthFacts Healthy() => new()
    {
        Now = Now,
        ClaudeDirectory = Path.Combine(Home, ".claude"),
        ClaudeDirectoryExists = true,
        TranscriptCount = 41,
        ClaudeCodeVersion = "2.1.325",
        Marker = new PluginMarker("2.3.0", "2.1.325", "terminal", Now.AddHours(-2)),
        NewestRecordAt = Now.AddMinutes(-1),
        PluginInstalled = true,
        Usage = Plan(),
        Mode = ClaudeSourceMode.Feed,
        SessionsToday = 3,
        StartAtLogin = true
    };

    private static UsageSnapshot Plan(bool stale = false, string source = ClaudeFeedUsageSource.Id) => new()
    {
        Mode = UsageMode.PlanLimits,
        FiveHourUtilizationPercent = 43.4,
        WeeklyUtilizationPercent = 12,
        FetchedAt = Now.AddSeconds(-12),
        SourceId = source,
        Trust = source == ClaudeFeedUsageSource.Id ? SourceTrust.Official : SourceTrust.Unofficial,
        IsStale = stale
    };

    private static HealthItem Item(HealthFacts facts, string id) => HealthCheck.Build(facts).Single(i => i.Id == id);

    [Fact]
    public void A_healthy_machine_lists_every_item_in_order_and_needs_nothing()
    {
        var items = HealthCheck.Build(Healthy());

        Assert.Equal(["claude-code", "claude-version", "feed", "limits", "legacy", "sessions", "notifications", "startup", "codex", "update"],
            items.Select(i => i.Id));
        Assert.DoesNotContain(items, i => i.Status is HealthStatus.Fail or HealthStatus.Warn);
        Assert.Equal("Found: ~" + Path.DirectorySeparatorChar + ".claude, 41 session transcripts.", items[0].Detail);
        Assert.Equal("2.1.325, from the newest session.", items[1].Detail);
        Assert.Equal("Connected (plugin 2.3.0), last update 1m ago.", items[2].Detail);
        Assert.Equal("5-hour 43% · weekly 12% from the Claude Code plugin (official), just now.", items[3].Detail);
        Assert.Equal("3 Claude Code sessions today.", items[5].Detail);
    }

    [Fact]
    public void Every_item_that_is_not_right_names_one_thing_to_do()
    {
        // Each fact turned bad on its own; Fail always has a button, Warn a button or an instruction.
        HealthFacts[] broken =
        [
            Healthy() with { ClaudeDirectoryExists = false, TranscriptCount = 0 },
            Healthy() with { ClaudeCodeVersion = "2.1.250" },
            Healthy() with { Marker = null, NewestRecordAt = null, PluginInstalled = false },
            Healthy() with { Marker = null, NewestRecordAt = null, PluginInstalled = true },
            Healthy() with { Marker = new PluginMarker("2.1.0", null, null, Now) },
            Healthy() with { Usage = new UsageSnapshot { RequiresReauth = true, FetchedAt = Now } },
            Healthy() with { Usage = new UsageSnapshot { IsStale = true, FetchedAt = Now, StatusDetail = ClaudeFeedUsageSource.WaitingDetail } },
            Healthy() with { Usage = new UsageSnapshot { IsStale = true, FetchedAt = Now, StatusDetail = "HTTP 500" } },
            Healthy() with { Usage = Plan(stale: true, source: ClaudeOAuthUsageSource.Id), Mode = ClaudeSourceMode.Legacy }
        ];
        var verbs = new[] { "install", "start", "send", "update", "run", "connect", "open", "check", "copy" };
        foreach (var facts in broken)
        {
            var items = HealthCheck.Build(facts).Where(i => i.Status is HealthStatus.Fail or HealthStatus.Warn).ToList();
            Assert.Single(items);
            var item = items[0];
            if (item.Status == HealthStatus.Fail)
                Assert.NotNull(item.ActionId);
            Assert.Equal(item.ActionId is null, item.ActionLabel is null);
            Assert.True(item.ActionId is not null || verbs.Any(v => item.Detail.Contains(v, StringComparison.OrdinalIgnoreCase)),
                $"{item.Id} says nothing to do: {item.Detail}");
        }
    }

    [Fact]
    public void Claude_Code_missing_says_how_to_get_it_and_mentions_wsl_on_windows_only()
    {
        var windows = Item(Healthy() with { ClaudeDirectoryExists = false, TranscriptCount = 0 }, "claude-code");
        Assert.Equal(HealthStatus.Fail, windows.Status);
        Assert.StartsWith(HealthCheck.NotFoundDetail, windows.Detail);
        Assert.Contains("WSL", windows.Detail);
        Assert.Equal(HealthActionIds.GetClaudeCode, windows.ActionId);
        Assert.Equal(HealthCheck.ClaudeCodeSetupUrl, HealthActionIds.Url(windows.ActionId));

        var mac = Item(Healthy() with { ClaudeDirectoryExists = false, TranscriptCount = 0, IsMac = true }, "claude-code");
        Assert.DoesNotContain("WSL", mac.Detail);

        // Installed but never used: nothing to get, only to start.
        var empty = Item(Healthy() with { TranscriptCount = 0 }, "claude-code");
        Assert.Equal((HealthStatus.Warn, null), (empty.Status, empty.ActionId));
        Assert.Contains("has no session transcripts yet: start Claude Code", empty.Detail);
    }

    [Theory]
    [InlineData(ClaudeSourceMode.Feed, false, HealthStatus.Fail)]
    [InlineData(ClaudeSourceMode.Auto, false, HealthStatus.Fail)]
    [InlineData(ClaudeSourceMode.Legacy, false, HealthStatus.Info)]
    [InlineData(ClaudeSourceMode.Off, false, HealthStatus.Info)]
    [InlineData(ClaudeSourceMode.Feed, true, HealthStatus.Warn)]
    [InlineData(ClaudeSourceMode.Legacy, true, HealthStatus.Info)]
    public void Without_the_plugin_only_the_modes_that_need_it_call_it_a_fault(ClaudeSourceMode mode, bool installed, HealthStatus expected)
    {
        var item = Item(Healthy() with { Mode = mode, Marker = null, NewestRecordAt = null, PluginInstalled = installed }, "feed");
        Assert.Equal(expected, item.Status);
        if (!installed)
            Assert.Equal(HealthActionIds.Connect, item.ActionId);
        if (expected == HealthStatus.Info && !installed)
            Assert.Equal(HealthCheck.NotConnectedOptionalDetail(mode), item.Detail);
        Assert.Contains("Plan limits are off", HealthCheck.NotConnectedOptionalDetail(ClaudeSourceMode.Off));
        Assert.Contains("Claude's usage service", HealthCheck.NotConnectedOptionalDetail(ClaudeSourceMode.Legacy));
    }

    [Theory]
    [InlineData("2.1.287", HealthStatus.Ok)]
    [InlineData("2.1.325", HealthStatus.Ok)]
    [InlineData("2.2.0", HealthStatus.Ok)]
    [InlineData("3.0", HealthStatus.Ok)]
    [InlineData("2.1.286", HealthStatus.Warn)]
    [InlineData("2.1.99", HealthStatus.Warn)]
    [InlineData("1.0.128", HealthStatus.Warn)]
    [InlineData(null, HealthStatus.Info)]
    [InlineData("unknown", HealthStatus.Info)]
    public void The_version_is_compared_as_numbers_not_text(string? version, HealthStatus expected)
    {
        var item = Item(Healthy() with { ClaudeCodeVersion = version }, "claude-version");
        Assert.Equal(expected, item.Status);
        if (expected == HealthStatus.Warn)
        {
            Assert.Equal($"Update Claude Code to use the official feed (yours: {version}; the plugin needs 2.1.287 or later).", item.Detail);
            Assert.Equal(HealthCheck.UpdateClaudeCodeCommand, HealthActionIds.CopyText(item.ActionId));
        }
    }

    [Theory]
    [InlineData("2.1.325", 2, 1, 325)]
    [InlineData("2.1.325 (Claude Code)", 2, 1, 325)]
    [InlineData("2.1.325-beta.2", 2, 1, 325)]
    [InlineData(" 2.2 ", 2, 2, -1)]
    public void Versions_parse_from_what_claude_prints(string text, int major, int minor, int build)
    {
        var version = HealthCheck.ParseVersion(text)!;
        Assert.Equal((major, minor, build), (version.Major, version.Minor, version.Build));
    }

    [Theory]
    [InlineData("")]
    [InlineData("claude")]
    [InlineData("2")]
    [InlineData("v2.1.3")]
    public void Not_a_version_is_null(string text) => Assert.Null(HealthCheck.ParseVersion(text));

    [Fact]
    public void The_feed_is_connected_by_a_record_or_the_marker_and_says_when_it_last_wrote()
    {
        var onlyMarker = Item(Healthy() with { NewestRecordAt = null, Marker = new PluginMarker("2.3.0", null, null, Now.AddHours(-3)) }, "feed");
        Assert.Equal(HealthStatus.Ok, onlyMarker.Status);
        Assert.Equal("Connected (plugin 2.3.0), last update 3h ago.", onlyMarker.Detail);

        var onlyRecord = Item(Healthy() with { Marker = null, NewestRecordAt = Now.AddSeconds(-5) }, "feed");
        Assert.Equal("Connected, last update just now.", onlyRecord.Detail);

        // A record from a clock ahead of the widget's reads "just now", never a negative age.
        var ahead = Item(Healthy() with { NewestRecordAt = Now.AddMinutes(3) }, "feed");
        Assert.Contains("just now", ahead.Detail);
    }

    [Fact]
    public void A_feed_that_never_wrote_offers_connect_unless_the_plugin_is_installed()
    {
        var never = Item(Healthy() with { Marker = null, NewestRecordAt = null, PluginInstalled = false }, "feed");
        Assert.Equal((HealthStatus.Fail, HealthCheck.NotConnectedDetail, HealthActionIds.Connect, "Connect"),
            (never.Status, never.Detail, never.ActionId, never.ActionLabel));

        var installed = Item(Healthy() with { Marker = null, NewestRecordAt = null, PluginInstalled = true }, "feed");
        Assert.Equal((HealthStatus.Warn, HealthCheck.InstalledNotConnectedDetail), (installed.Status, installed.Detail));
        Assert.Contains("organization may not allow user plugins", installed.Detail);
    }

    [Theory]
    [InlineData("2.0.0", HealthStatus.Warn)]
    [InlineData("2.1.9", HealthStatus.Warn)]
    [InlineData("2.1.300", HealthStatus.Warn)]
    [InlineData("2.2.0", HealthStatus.Ok)]
    [InlineData("2.10.0", HealthStatus.Ok)]
    [InlineData(null, HealthStatus.Ok)]
    public void A_plugin_too_old_for_the_lights_asks_for_an_update(string? version, HealthStatus expected)
    {
        var item = Item(Healthy() with { Marker = new PluginMarker(version, null, null, Now) }, "feed");
        Assert.Equal(expected, item.Status);
        if (expected == HealthStatus.Warn)
            Assert.Equal("claude plugin update sticky-usage@sticky-ai-usage", HealthActionIds.CopyText(item.ActionId));
    }

    [Fact]
    public void Limits_say_where_the_numbers_come_from_and_how_old_they_are()
    {
        var legacy = Item(Healthy() with { Usage = Plan(source: ClaudeOAuthUsageSource.Id), Mode = ClaudeSourceMode.Legacy }, "limits");
        Assert.Equal("5-hour 43% · weekly 12% from Claude's usage service (unofficial), just now.", legacy.Detail);

        var monthly = Item(Healthy() with
        {
            Usage = new UsageSnapshot
            {
                Mode = UsageMode.MonthlySpend, HasMonthlySpend = true, MonthlySpendUsedUsd = 96.75m, MonthlySpendLimitUsd = 200,
                MonthlySpendPercent = 48.4, FetchedAt = Now.AddMinutes(-2), SourceId = ClaudeOAuthUsageSource.Id
            }
        }, "limits");
        Assert.Equal("$96.75 of $200.00 (48%) from Claude's usage service (unofficial), 2m ago.", monthly.Detail);

        var percentOnly = Item(Healthy() with
        {
            Usage = new UsageSnapshot
            {
                Mode = UsageMode.MonthlySpend, HasMonthlySpend = true, MonthlySpendPercentOnly = true, MonthlySpendPercent = 63,
                FetchedAt = Now, SourceId = ClaudeFeedUsageSource.Id
            }
        }, "limits");
        Assert.StartsWith("63% of your spend limit from the Claude Code plugin", percentOnly.Detail);
    }

    [Fact]
    public void Limits_in_every_state()
    {
        Assert.Equal((HealthStatus.Fail, HealthActionIds.CopySignIn),
            Pick(Healthy() with { Usage = new UsageSnapshot { RequiresReauth = true } }));
        Assert.Equal("claude auth login", HealthActionIds.CopyText(HealthActionIds.CopySignIn));
        Assert.Equal((HealthStatus.Warn, null),
            Pick(Healthy() with { Usage = new UsageSnapshot { IsStale = true, StatusDetail = ClaudeFeedUsageSource.NoLimitsDetail } }));
        Assert.Equal((HealthStatus.Fail, HealthActionIds.OpenLogFolder),
            Pick(Healthy() with { Usage = new UsageSnapshot { IsStale = true, StatusDetail = "HTTP 503" } }));
        Assert.Equal((HealthStatus.Warn, HealthActionIds.OpenLogFolder), Pick(Healthy() with { Usage = Plan(stale: true) }));
        Assert.Equal((HealthStatus.Info, HealthActionIds.OpenUsageSource), Pick(Healthy() with { Mode = ClaudeSourceMode.Off }));
        Assert.Equal((HealthStatus.Info, null), Pick(Healthy() with { Usage = null }));
        Assert.Equal(HealthCheck.NoLimitsDetail,
            Item(Healthy() with { Usage = new UsageSnapshot { IsStale = true, StatusDetail = ClaudeFeedUsageSource.WaitingDetail } }, "limits").Detail);

        static (HealthStatus, string?) Pick(HealthFacts facts) => Item(facts, "limits") is var item ? (item.Status, item.ActionId) : default;
    }

    [Theory]
    [InlineData(ClaudeSourceMode.Feed, HealthStatus.Ok, false)]
    [InlineData(ClaudeSourceMode.Off, HealthStatus.Ok, false)]
    [InlineData(ClaudeSourceMode.Auto, HealthStatus.Info, true)]
    [InlineData(ClaudeSourceMode.Legacy, HealthStatus.Info, true)]
    public void Legacy_mode_is_disclosed_whenever_it_reads_the_sign_in(ClaudeSourceMode mode, HealthStatus status, bool disclosed)
    {
        var item = Item(Healthy() with { Mode = mode }, "legacy");
        Assert.Equal(status, item.Status);
        Assert.Equal(disclosed, item.Detail.Contains(ViewModels.UsageSourceOptions.UnofficialWarning));
    }

    [Fact]
    public void Sessions_notifications_startup_codex_and_updates_are_facts_with_their_buttons()
    {
        Assert.Equal("No Claude Code sessions today yet.", Item(Healthy() with { SessionsToday = 0 }, "sessions").Detail);
        Assert.Equal("1 Claude Code session today.", Item(Healthy() with { SessionsToday = 1 }, "sessions").Detail);
        Assert.Equal("Not counted yet.", Item(Healthy() with { SessionsToday = null }, "sessions").Detail);

        var alerts = Item(Healthy(), "notifications");
        Assert.Equal((HealthActionIds.SendTestAlert, "Send a test alert"), (alerts.ActionId, alerts.ActionLabel));
        Assert.Contains("A test alert was sent at", Item(Healthy() with { LastTestAlertAt = Now }, "notifications").Detail);
        Assert.Contains("Script Editor", Item(Healthy() with { IsMac = true }, "notifications").Detail);
        Assert.Null(Item(Healthy() with { AlertsEnabled = false }, "notifications").ActionId);

        Assert.Equal(("Start with Windows", "Turn off"), (Item(Healthy(), "startup").Title, Item(Healthy(), "startup").ActionLabel));
        var macOff = Item(Healthy() with { IsMac = true, StartAtLogin = false }, "startup");
        Assert.Equal(("Launch at login", "Turn on", HealthActionIds.ToggleStartup), (macOff.Title, macOff.ActionLabel, macOff.ActionId));

        Assert.StartsWith("Not found on this computer", Item(Healthy(), "codex").Detail);
        Assert.Equal("Found: its limits show beside Claude's.", Item(Healthy() with { CodexPresent = true }, "codex").Detail);
        Assert.Equal("Found, turned off in Settings.", Item(Healthy() with { CodexPresent = true, CodexEnabled = false }, "codex").Detail);

        Assert.StartsWith("Off: the app makes no network request", Item(Healthy(), "update").Detail);
        Assert.Contains("price list is checked once a day", Item(Healthy() with { CheckForUpdates = true }, "update").Detail);
        Assert.Equal(HealthCheck.ReleasesUrl, HealthActionIds.Url(Item(Healthy(), "update").ActionId));
    }

    [Fact]
    public void The_report_has_the_build_versions_sources_and_statuses_and_no_home_folder()
    {
        var facts = Healthy() with
        {
            ClaudeDirectory = Path.Combine(Home, "custom-claude"),
            CodexSessionsFolder = Path.Combine(Home, ".codex", "sessions"),
            Mode = ClaudeSourceMode.Auto
        };
        var report = HealthCheck.Report(HealthCheck.Build(facts), "Sticky AI Usage 2.0.0 (abc1234) · profile dev · usage source: Automatic", Now);

        Assert.StartsWith("Sticky AI Usage health report", report);
        Assert.Contains("Sticky AI Usage 2.0.0 (abc1234) · profile dev · usage source: Automatic", report);
        Assert.Contains(".NET " + Environment.Version, report);
        Assert.Contains("[OK] Claude Code version: 2.1.325", report);
        Assert.Contains("[OK] Claude Code plugin: Connected (plugin 2.3.0)", report);
        Assert.Contains("from the Claude Code plugin (official)", report);
        Assert.Contains("[Info] Legacy mode: On (Automatic).", report);
        Assert.Contains("~" + Path.DirectorySeparatorChar + "custom-claude", report);
        Assert.DoesNotContain(Home, report, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, report.Split('\n').Count(l => l.StartsWith('[')));
    }

    [Fact]
    public void The_report_hides_the_home_folder_even_in_an_item_that_names_it()
    {
        var item = new HealthItem("x", "Folder", HealthStatus.Info, Path.Combine(Home, "somewhere") + " and " + Path.Combine(Home, "else"));
        var report = HealthCheck.Report([item], "test", Now);
        Assert.DoesNotContain(Home, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("~" + Path.DirectorySeparatorChar + "somewhere", report);
    }

    [Fact]
    public void Items_read_well_to_a_screen_reader()
    {
        var item = Item(Healthy() with { Marker = null, NewestRecordAt = null, PluginInstalled = false }, "feed");
        Assert.Equal("Claude Code plugin: Action needed. " + HealthCheck.NotConnectedDetail, item.ToString());
    }
}
