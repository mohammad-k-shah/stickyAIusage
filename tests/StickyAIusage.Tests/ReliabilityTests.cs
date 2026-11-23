using System.Reflection;
using System.Text.Json.Nodes;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>The theme and the Windows position as settings: a new install
/// follows the computer's theme and docks; older files keep what they ran with.</summary>
public class ThemeAndPositionSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.Combine("settings.json");

    [Fact]
    public void A_new_install_follows_the_computer_s_theme_and_docks()
    {
        var settings = SettingsService.Load(SettingsPath);

        Assert.Equal("system", settings.Theme);
        Assert.True(settings.WidgetDocked);
    }

    [Theory]
    [InlineData("""{ "DayTargetUsd": 12 }""")]                                        // v1: no version key
    [InlineData("""{ "SchemaVersion": 2, "DayTargetUsd": 12 }""")]
    [InlineData("""{ "SchemaVersion": 3, "ClaudeSource": "feed", "DayTargetUsd": 12 }""")]
    public void An_older_file_without_a_theme_keeps_the_dark_it_ran_with(string json)
    {
        File.WriteAllText(SettingsPath, json);

        var settings = SettingsService.Load(SettingsPath);

        Assert.Equal("dark", settings.Theme);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("system")]
    [InlineData("dark")]
    public void A_chosen_theme_survives_a_restart(string theme)
    {
        File.WriteAllText(SettingsPath, $$"""{ "SchemaVersion": 3, "ClaudeSource": "feed", "Theme": "{{theme}}" }""");
        var settings = SettingsService.Load(SettingsPath);
        SettingsService.Save(settings, SettingsPath);

        Assert.Equal(theme, SettingsService.Load(SettingsPath).Theme);
        Assert.Equal(theme, JsonNode.Parse(File.ReadAllText(SettingsPath))!["Theme"]!.GetValue<string>());
    }

    [Fact]
    public void A_folder_with_an_older_version_s_cache_but_no_settings_keeps_dark()
    {
        Directory.CreateDirectory(_dir.Combine("cache"));
        File.WriteAllText(_dir.Combine("cache", "claude-usage.json"), "{}");

        Assert.Equal("dark", SettingsService.Load(SettingsPath).Theme);
    }

    [Fact]
    public void An_upgrade_starts_docked_as_before()
    {
        // v1 wrote the dock position on every start, but never WidgetDocked.
        File.WriteAllText(SettingsPath, """{ "SchemaVersion": 3, "ClaudeSource": "auto", "WidgetRight": 2548, "WidgetBottom": 1532 }""");

        var settings = SettingsService.Load(SettingsPath);

        Assert.True(settings.WidgetDocked);
        Assert.Null(WidgetPosition.Saved(settings, Screen, 12, 32));
    }

    private static readonly Box Screen = new(0, 0, 2560, 1600);

    [Fact]
    public void A_dragged_position_is_where_the_next_start_puts_the_pill()
    {
        var settings = new AppSettings { WidgetDocked = false, WidgetRight = 1212, WidgetBottom = 812 };

        Assert.Equal((1200.0, 800.0), WidgetPosition.Saved(settings, Screen, windowMargin: 12, pillHeight: 32));
    }

    [Theory]
    [InlineData(-500, 800)]     // a monitor left of this one, now unplugged
    [InlineData(3500, 800)]     // right of the screen
    [InlineData(1200, 2400)]    // below it (the resolution dropped)
    [InlineData(1200, 20)]      // above: the pill would be cut off at the top
    [InlineData(30, 800)]       // only a sliver of the pill left on the screen
    public void A_saved_spot_off_every_screen_docks_instead(double right, double bottom)
    {
        var settings = new AppSettings { WidgetDocked = false, WidgetRight = right + 12, WidgetBottom = bottom + 12 };

        Assert.Null(WidgetPosition.Saved(settings, Screen, 12, 32));
    }

    [Fact]
    public void A_second_monitor_left_of_the_main_one_counts_as_screen()
    {
        var twoScreens = new Box(-1920, 0, 1920 + 2560, 1600);
        var settings = new AppSettings { WidgetDocked = false, WidgetRight = -600 + 12, WidgetBottom = 900 + 12 };

        Assert.Equal((-600.0, 900.0), WidgetPosition.Saved(settings, twoScreens, 12, 32));
    }

    [Theory]
    [InlineData(true, 1212.0, 812.0)]           // docked wins over a saved spot
    [InlineData(false, null, 812.0)]            // half a position
    [InlineData(false, double.NaN, 812.0)]      // hand-edited nonsense
    public void Without_a_whole_saved_spot_it_docks(bool docked, double? right, double bottom)
    {
        var settings = new AppSettings { WidgetDocked = docked, WidgetRight = right, WidgetBottom = bottom };

        Assert.Null(WidgetPosition.Saved(settings, Screen, 12, 32));
    }
}

public class UnhandledErrorsTests
{
    private readonly List<string> _lines = [];
    private DateTimeOffset _now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private UnhandledErrors Errors() => new(_lines.Add, () => _now);

    [Fact]
    public void A_UI_thread_exception_is_logged_and_handled()
    {
        var handled = Errors().OnUiThread(new InvalidOperationException("boom"));

        Assert.True(handled);
        var line = Assert.Single(_lines);
        Assert.Contains("UI thread", line);
        Assert.Contains("InvalidOperationException: boom", line);
    }

    [Fact]
    public void A_fault_on_every_frame_writes_ten_lines_a_minute_and_then_a_count()
    {
        var errors = Errors();
        for (var i = 0; i < 500; i++)
        {
            errors.OnUiThread(new InvalidOperationException("render " + i));
            _now = _now.AddMilliseconds(16);
        }
        Assert.Equal(UnhandledErrors.BurstLimit, _lines.Count);

        _now = _now.AddMinutes(1);
        errors.OnUiThread(new InvalidOperationException("later"));

        Assert.Contains(_lines, l => l.StartsWith("490 more unhandled exception(s)", StringComparison.Ordinal));
        Assert.Contains("later", _lines[^1]);
    }

    [Fact]
    public void A_terminating_exception_is_always_written()
    {
        var errors = Errors();
        for (var i = 0; i < 20; i++)
            errors.OnUiThread(new InvalidOperationException("noise"));

        errors.OnFatal(new OutOfMemoryException("gone"), terminating: true);
        errors.OnStartupFailed(new IOException("locked"));

        Assert.Contains(_lines, l => l.Contains("has to close") && l.Contains("gone"));
        Assert.Contains(_lines, l => l.StartsWith("Start-up failed", StringComparison.Ordinal) && l.Contains("locked"));
    }

    [Fact]
    public void A_fatal_event_without_an_exception_object_still_says_so() =>
        Assert.Contains("(no exception object)", Assert.Single(LogFatal()));

    private List<string> LogFatal()
    {
        Errors().OnFatal(null, terminating: true);
        return _lines;
    }
}

public class AppIdentityTests
{
    [Theory]
    [InlineData("2.0.0+3967cd3a1b2c4d5e6f", "2.0.0 (3967cd3)")]
    [InlineData("2.0.0-beta.1+abc", "2.0.0-beta.1 (abc)")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+", "1.0.0")]
    [InlineData(null, "unknown version")]
    [InlineData("", "unknown version")]
    public void The_version_names_the_commit_it_was_built_from(string? informational, string expected) =>
        Assert.Equal(expected, AppIdentity.FormatVersion(informational));

    [Fact]
    public void The_running_build_has_a_version() =>
        Assert.Matches(@"^\d+\.\d+\.\d+", AppIdentity.Version(typeof(AppIdentity).Assembly));

    private static AppLocations Locations(Func<string, string?> env, params string[] args) =>
        AppPaths.Resolve(env, args, isMac: false);

    [Fact]
    public void The_line_names_version_profile_and_source()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        string? Env(string name) => name == "LOCALAPPDATA" ? home : null;
        var settings = new AppSettings { ClaudeSource = "auto" };

        Assert.Equal("Sticky AI Usage 2.0.0 (3967cd3) · default profile · usage source: Automatic",
            AppIdentity.Describe("2.0.0 (3967cd3)", Locations(Env), settings));
        Assert.Equal("Sticky AI Usage 2.0.0 · profile dev · usage source: Plugin",
            AppIdentity.Describe("2.0.0", Locations(Env, "--profile", "dev"), new AppSettings()));

        var line = AppIdentity.StartLine("Windows", "2.0.0", Locations(Env), new AppSettings { ClaudeSource = "legacy" });
        Assert.StartsWith("App started (Windows): Sticky AI Usage 2.0.0 · default profile · usage source: Legacy · .NET ", line);
    }

    [Fact]
    public void A_custom_data_folder_says_so()
    {
        string? Env(string name) => name == AppPaths.DataDirectoryVariable ? Path.Combine(Path.GetTempPath(), "custom") : null;
        Assert.Equal("custom data folder", AppIdentity.Profile(Locations(Env)));
    }
}

/// <summary>Every state that isn't current gives the user exactly one next
/// step: the notice or the health line, never both and never neither; in all
/// three layouts.</summary>
public class HealthLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private static WidgetViewModel View(UsageSnapshot usage, AppSettings? settings = null) =>
        WidgetViewModel.From(usage, null, settings ?? new AppSettings { PluginTipDismissed = true }, Now, TimeZoneInfo.Utc);

    private static UsageSnapshot Monthly(bool stale = false, string? source = null) => new()
    {
        HasMonthlySpend = true, MonthlySpendUsedUsd = 50, MonthlySpendLimitUsd = 200, MonthlySpendPercent = 25,
        FetchedAt = Now.AddMinutes(stale ? -12 : 0), IsStale = stale, SourceId = source ?? ClaudeOAuthUsageSource.Id
    };

    private static UsageSnapshot Plan(bool stale = false, string? source = null, TimeSpan? age = null) => new()
    {
        Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 40, FiveHourResetsAt = Now.AddHours(2),
        WeeklyUtilizationPercent = 20, WeeklyResetsAt = Now.AddDays(3),
        FetchedAt = Now - (age ?? (stale ? TimeSpan.FromMinutes(12) : TimeSpan.Zero)), IsStale = stale,
        SourceId = source ?? ClaudeOAuthUsageSource.Id
    };

    private static UsageSnapshot NoReading(string? detail) => new() { IsStale = true, FetchedAt = Now, StatusDetail = detail };

    public static TheoryData<string, UsageSnapshot, UsageHealth?, HealthAction?, bool> States => new()
    {
        // name, reading, health line (null: none), its action, a notice instead
        { "monthly ok", Monthly(), null, null, false },
        { "plan ok", Plan(), null, null, false },
        { "monthly stale", Monthly(stale: true), UsageHealth.Stale, HealthAction.OpenLogFolder, false },
        { "plan stale", Plan(stale: true), UsageHealth.Stale, HealthAction.OpenLogFolder, false },
        { "plan from the feed, 12 min old", Plan(source: ClaudeFeedUsageSource.Id, age: TimeSpan.FromMinutes(12)), UsageHealth.Ok, HealthAction.None, false },
        { "monthly sign-in", Monthly(stale: true) with { RequiresReauth = true }, null, null, true },
        { "plan sign-in", Plan(stale: true) with { RequiresReauth = true }, null, null, true },
        { "unknown sign-in", NoReading(null) with { RequiresReauth = true }, null, null, true },
        { "unknown waiting for the plugin", NoReading(ClaudeFeedUsageSource.WaitingDetail), null, null, true },
        { "unknown no limits yet", NoReading(ClaudeFeedUsageSource.NoLimitsDetail), UsageHealth.Unavailable, HealthAction.None, false },
        { "unknown usage off", NoReading(AppServices.UsageOffDetail), UsageHealth.Unavailable, HealthAction.OpenSettings, false },
        { "unknown failed", NoReading(null), UsageHealth.Unavailable, HealthAction.OpenHealth, false }
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Each_state_gives_one_next_step(string name, UsageSnapshot usage, UsageHealth? health, HealthAction? action, bool notice)
    {
        var view = View(usage);

        Assert.True(name.Length > 0);
        Assert.Equal(notice, view.Notice is not null);
        Assert.Equal(health, view.Health?.Health);
        Assert.Equal(action, view.Health?.Action);
        if (view.Health is { } line)
        {
            Assert.False(string.IsNullOrWhiteSpace(line.Detail));
            Assert.Equal(line.Action == HealthAction.None, line.ActionLabel is null);
        }
        Assert.False(view.Notice is { Kind: not FlyoutNoticeKind.PluginTip } && view.Health is not null, "two answers to one problem");
        if (usage.ToProviderUsage().Health != UsageHealth.Ok)
            Assert.True(view.Notice is not null || view.Health is not null, "a problem without a next step");
    }

    [Fact]
    public void The_plugin_tip_does_not_hide_why_the_numbers_are_old()
    {
        // Pro/Max from the usage endpoint, tip not dismissed, and the last fetch failed.
        var view = View(Plan(stale: true), new AppSettings());

        Assert.Equal(FlyoutNoticeKind.PluginTip, view.Notice!.Kind);
        Assert.Equal(UsageHealth.Stale, view.Health!.Health);
        Assert.Null(View(Plan(), new AppSettings()).Health);
    }

    [Fact]
    public void No_tooltip_sends_the_user_to_a_log_folder_without_a_reason()
    {
        var view = View(NoReading(null));

        Assert.DoesNotContain("log folder from the menu", view.UsageTooltip);
        Assert.Equal(HealthLine.FailedDetail, view.Health!.Detail);
        Assert.Equal("Usage", view.SummaryTitle); // the card holds only the health line
    }

    [Fact]
    public void The_sign_in_tooltip_names_the_command_that_renews_it()
    {
        var tooltip = View(NoReading(null) with { RequiresReauth = true }).UsageTooltip;

        Assert.Contains("claude auth login", tooltip);
        Assert.DoesNotContain("Claude Desktop", tooltip);
    }

    [Fact]
    public void The_feed_line_says_how_old_the_numbers_are()
    {
        var line = View(Plan(source: ClaudeFeedUsageSource.Id, age: TimeSpan.FromMinutes(12))).Health!;

        Assert.StartsWith("As of 12m ago", line.Detail);
    }
}
