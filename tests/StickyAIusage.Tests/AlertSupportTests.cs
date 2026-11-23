using System.Globalization;
using System.Text.Json;
using StickyAIusage.Alerts;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Quiet hours, the notification text, the macOS banner's
/// AppleScript, the mute item, the settings, and the refresh that plans alerts.</summary>
public class AlertSupportTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    // ---------------------------------------------------------- quiet hours

    [Theory]
    [InlineData("22:00", 22, 0)]
    [InlineData("7:30", 7, 30)]
    [InlineData("07:05", 7, 5)]
    [InlineData("7", 7, 0)]
    [InlineData("23", 23, 0)]
    [InlineData("10 PM", 22, 0)]
    [InlineData("10:30pm", 22, 30)]
    [InlineData("12 am", 0, 0)]
    [InlineData("7 a.m.", 7, 0)]
    [InlineData("22.30", 22, 30)]
    [InlineData(" 6:00 ", 6, 0)]
    public void Typed_times_are_understood(string text, int hour, int minute)
    {
        Assert.True(QuietHours.TryParseTime(text, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("25:00")]
    [InlineData("7:60")]
    [InlineData("noon")]
    [InlineData("-1")]
    public void Nonsense_times_are_refused(string? text) => Assert.False(QuietHours.TryParseTime(text, out _));

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    public void Quiet_hours_may_cross_midnight(int hour, int minute, bool quiet)
    {
        var hours = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0));
        Assert.Equal(quiet, hours.Contains(new DateTimeOffset(2026, 11, 18, hour, minute, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(12, 59, false)]
    [InlineData(13, 0, true)]
    [InlineData(13, 59, true)]
    [InlineData(14, 0, false)]
    public void Quiet_hours_within_one_day(int hour, int minute, bool quiet)
    {
        var hours = new QuietHours(new TimeOnly(13, 0), new TimeOnly(14, 0));
        Assert.Equal(quiet, hours.Contains(new DateTimeOffset(2026, 11, 18, hour, minute, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Quiet_hours_are_local_time()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
        var hours = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0));
        // 17:00 UTC is 22:30 in India.
        Assert.True(hours.Contains(new DateTimeOffset(2026, 11, 18, 17, 0, 0, TimeSpan.Zero), india));
        Assert.False(hours.Contains(new DateTimeOffset(2026, 11, 18, 17, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Settings_check_quiet_hours_only_while_they_are_on()
    {
        Assert.True(QuietHours.Validate(false, "", "nonsense").IsValid);
        var ok = QuietHours.Validate(true, "22:00", "7:00");
        Assert.True(ok.IsValid);
        Assert.Equal((new TimeOnly(22, 0), new TimeOnly(7, 0)), (ok.From, ok.To));

        var bad = QuietHours.Validate(true, "22:00", "later");
        Assert.False(bad.IsValid);
        Assert.False(bad.FromInvalid);
        Assert.True(bad.ToInvalid);
        Assert.Equal("Enter times like 22:00 or 7:30.", bad.Error);

        var same = QuietHours.Validate(true, "7:00", "07:00");
        Assert.False(same.IsValid);
        Assert.Equal("Quiet hours need a different start and end.", same.Error);
    }

    [Fact]
    public void Quiet_hours_come_from_the_settings_file_or_not_at_all()
    {
        Assert.Equal(new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0)), QuietHours.FromSettings("22:00", "07:00"));
        Assert.Null(QuietHours.FromSettings(null, null));
        Assert.Null(QuietHours.FromSettings("22:00", null));
        Assert.Null(QuietHours.FromSettings("22:00", "22:00"));
        Assert.Null(QuietHours.FromSettings("bedtime", "07:00"));
        Assert.Equal("07:05", QuietHours.ToSetting(new TimeOnly(7, 5)));
    }

    // ---------------------------------------------------------- one notification per refresh

    private static Alert A(string title, UsageLevel level, string body = "Body.") => new(title, AlertKind.Threshold, title, body, level);

    [Fact]
    public void One_alert_is_shown_as_it_is()
    {
        Assert.Equal(("5-hour window at 82%", "Resets today 4:30 PM."),
            AlertMessages.ForNotification([A("5-hour window at 82%", UsageLevel.Orange, "Resets today 4:30 PM.")]));
    }

    [Fact]
    public void Several_alerts_lead_with_the_most_urgent_and_name_the_rest()
    {
        var (title, body) = AlertMessages.ForNotification(
        [
            A("You're back", UsageLevel.Green, "The weekly window has reset."),
            A("5-hour limit reached", UsageLevel.Red, "Resets today 4:30 PM."),
            A("Weekly window at 81%", UsageLevel.Orange)
        ]);
        Assert.Equal("5-hour limit reached", title);
        Assert.Equal("Resets today 4:30 PM. Also: Weekly window at 81%; You're back.", body);
    }

    [Fact]
    public void A_dev_profile_says_so_in_the_title()
    {
        Assert.Equal("Test alert (profile dev)", AlertMessages.ForNotification([AlertPlanner.TestAlert()], "profile dev").Title);
    }

    [Fact]
    public void Texts_stay_within_what_Windows_takes_and_never_split_an_emoji()
    {
        var (title, body) = AlertMessages.ForNotification([A(new string('t', 100), UsageLevel.Red, new string('b', 300))]);
        Assert.Equal(AlertMessages.MaxTitle, title.Length);
        Assert.Equal(AlertMessages.MaxBody, body.Length);
        Assert.EndsWith("…", body);

        // The cut falls between the two halves of the first emoji.
        var emoji = new string('x', AlertMessages.MaxTitle - 2) + "😀😀";
        var cut = AlertMessages.ForNotification([A(emoji, UsageLevel.Red)]).Title;
        Assert.True(cut.Length <= AlertMessages.MaxTitle);
        Assert.False(char.IsHighSurrogate(cut[^2]));
        Assert.Equal(new string('x', AlertMessages.MaxTitle - 2) + "…", cut);
    }

    [Fact]
    public void No_alerts_is_a_mistake_of_the_caller() =>
        Assert.Throws<ArgumentException>(() => AlertMessages.ForNotification([]));

    // ---------------------------------------------------------- AppleScript

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\path", "\"C:\\\\path\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("two\nlines\r\nand\ttab", "\"two lines  and tab\"")]
    [InlineData("Café ✓ 日本語 😀", "\"Café ✓ 日本語 😀\"")]
    [InlineData("bell\u0007", "\"bell\"")]
    public void AppleScript_strings_are_escaped(string text, string literal) =>
        Assert.Equal(literal, AppleScriptNotification.Literal(text));

    [Fact]
    public void The_banner_is_one_display_notification_argument()
    {
        Assert.Equal(["-e", "display notification \"Resets \\\"soon\\\".\" with title \"You're back\""],
            AppleScriptNotification.Arguments("You're back", "Resets \"soon\"."));
    }

    [Fact]
    public void A_quote_cannot_end_the_string_and_run_a_command()
    {
        var script = AppleScriptNotification.Arguments("x\" & (do shell script \"touch /tmp/p\") & \"", "b")[1];
        // Every quote inside the title is escaped: the only unescaped quotes are the four delimiters.
        var unescaped = 0;
        for (var i = 0; i < script.Length; i++)
        {
            if (script[i] != '"')
                continue;
            var backslashes = 0;
            for (var j = i - 1; j >= 0 && script[j] == '\\'; j--)
                backslashes++;
            if (backslashes % 2 == 0)
                unescaped++;
        }
        Assert.Equal(4, unescaped);
    }

    // ---------------------------------------------------------- mute

    [Fact]
    public void The_mute_item_mutes_for_an_hour_and_unmutes()
    {
        var settings = new AppSettings();
        var now = new DateTimeOffset(2026, 11, 18, 16, 30, 0, TimeSpan.Zero);
        Assert.Equal("Mute alerts for an hour", AlertMute.MenuLabel(settings, now, TimeZoneInfo.Utc, EnUs));

        AlertMute.Toggle(settings, now);
        Assert.Equal(now.AddHours(1), settings.AlertsMutedUntil);
        Assert.True(AlertMute.IsMuted(settings, now.AddMinutes(59)));
        Assert.Equal("Unmute alerts (muted until 5:30 PM)", AlertMute.MenuLabel(settings, now, TimeZoneInfo.Utc, EnUs));
        Assert.True(AlertSettings.From(settings).IsSilenced(now.AddMinutes(30), TimeZoneInfo.Utc));

        AlertMute.Toggle(settings, now.AddMinutes(10));
        Assert.Null(settings.AlertsMutedUntil);
        Assert.False(AlertSettings.From(settings).IsSilenced(now.AddMinutes(30), TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_mute_that_ran_out_reads_as_not_muted()
    {
        var now = new DateTimeOffset(2026, 11, 18, 16, 30, 0, TimeSpan.Zero);
        var settings = new AppSettings { AlertsMutedUntil = now.AddMinutes(-1) };
        Assert.False(AlertMute.IsMuted(settings, now));
        AlertMute.Toggle(settings, now);
        Assert.Equal(now.AddHours(1), settings.AlertsMutedUntil);
    }

    // ---------------------------------------------------------- settings

    [Fact]
    public void Alert_settings_default_on_and_an_older_file_gets_the_defaults()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":4,\"ClaudeSource\":\"feed\",\"OrangeThresholdPercent\":75}");
        var loaded = SettingsService.Load(path);
        var alerts = AlertSettings.From(loaded);
        Assert.True(alerts is { Enabled: true, OnThreshold: true, OnForecast: true, OnLimit: true, OnReset: true });
        Assert.Equal(75, alerts.OrangePercent);
        Assert.Equal(TimeSpan.FromMinutes(30), alerts.ForecastLead);
        Assert.Null(alerts.Quiet);
        Assert.Null(alerts.MutedUntil);
    }

    [Fact]
    public void Alert_settings_survive_a_round_trip()
    {
        var path = _dir.Combine("settings.json");
        var muted = new DateTimeOffset(2026, 11, 18, 17, 30, 0, TimeSpan.FromHours(5.5));
        SettingsService.Save(new AppSettings
        {
            AlertsEnabled = false, AlertOnThreshold = false, AlertOnForecast = false, AlertOnLimit = false, AlertOnReset = false,
            AlertForecastLeadMinutes = 45, AlertQuietFrom = "22:00", AlertQuietTo = "07:00", AlertsMutedUntil = muted
        }, path);
        var loaded = SettingsService.Load(path);
        Assert.False(loaded.AlertsEnabled || loaded.AlertOnThreshold || loaded.AlertOnForecast || loaded.AlertOnLimit || loaded.AlertOnReset);
        Assert.Equal(45, loaded.AlertForecastLeadMinutes);
        Assert.Equal(("22:00", "07:00"), (loaded.AlertQuietFrom, loaded.AlertQuietTo));
        Assert.Equal(muted, loaded.AlertsMutedUntil);
        Assert.Equal(TimeSpan.FromMinutes(45), AlertSettings.From(loaded).ForecastLead);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 300)]
    public void A_lead_time_out_of_range_is_kept_in_range(int minutes, int expected) =>
        Assert.Equal(TimeSpan.FromMinutes(expected), AlertSettings.From(new AppSettings { AlertForecastLeadMinutes = minutes }).ForecastLead);

    // ---------------------------------------------------------- wiring

    private static (IClaudeUsageSource, IReadOnlyList<IDisposable>) NoEndpoint(Func<DateTimeOffset> _) =>
        throw new InvalidOperationException("Feed mode never builds the endpoint.");

    private void WriteRecord(double fiveHour)
    {
        var now = DateTimeOffset.UtcNow;
        var ms = now.ToUnixTimeMilliseconds();
        Directory.CreateDirectory(_dir.Combine("feed"));
        File.WriteAllText(_dir.Combine("feed", "claude-s1.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema"] = 1, ["source"] = "claude-code-mod", ["sessionId"] = "s1", ["cwd"] = "C:/work",
            ["observedAt"] = ms, ["startedAt"] = ms - 60_000, ["rateLimitsAt"] = ms, ["state"] = "idle", ["stateSince"] = ms,
            ["rateLimits"] = new object[]
            {
                new { kind = "five_hour", percentUsed = fiveHour, resetsAt = now.AddHours(2).ToString("O", CultureInfo.InvariantCulture) }
            }
        }));
    }

    private AppServices Services(AppSettings settings) =>
        AppServices.Create(settings, new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false,
            NoEndpoint, cacheDirectory: _dir.Combine("cache"));

    [Fact]
    public async Task A_refresh_plans_the_alerts_and_a_restart_does_not_repeat_them()
    {
        WriteRecord(86);
        var settings = new AppSettings { ClaudeSource = "feed" };
        using (var services = Services(settings))
        {
            var summary = await services.Feed.GetSummaryAsync();
            Assert.Equal("5-hour window at 86%", Assert.Single(summary.Alerts).Title);
            Assert.Empty((await services.Feed.GetSummaryAsync()).Alerts);
        }
        Assert.True(File.Exists(_dir.Combine("cache", AlertStateFile.FileName)));

        using var restarted = Services(settings);
        Assert.Empty((await restarted.Feed.GetSummaryAsync()).Alerts);
    }

    [Fact]
    public async Task Settings_changed_after_the_services_were_built_apply_at_the_next_refresh()
    {
        WriteRecord(86);
        var settings = new AppSettings { ClaudeSource = "feed", AlertsEnabled = false };
        using var services = Services(settings);
        Assert.Empty((await services.Feed.GetSummaryAsync()).Alerts);

        settings.AlertsEnabled = true; // what Settings → Save does to the shared object
        Assert.Single((await services.Feed.GetSummaryAsync()).Alerts);
    }

    [Fact]
    public async Task Without_a_reading_nothing_is_written()
    {
        using var services = Services(new AppSettings { ClaudeSource = "feed" });
        Assert.Empty((await services.Feed.GetSummaryAsync()).Alerts);
        Assert.False(File.Exists(_dir.Combine("cache", AlertStateFile.FileName)));
    }
}
