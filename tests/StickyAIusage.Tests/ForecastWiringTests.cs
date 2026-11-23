using System.Globalization;
using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>The forecast from the plugin's feed to the window: every refresh
/// records the reading, hands back its forecasts and writes forecast.json.</summary>
public class ForecastWiringTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static (IClaudeUsageSource, IReadOnlyList<IDisposable>) NoEndpoint(Func<DateTimeOffset> _) =>
        throw new InvalidOperationException("Feed mode never builds the endpoint.");

    private void WriteRecord(double fiveHour, double weekly)
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
                new { kind = "five_hour", percentUsed = fiveHour, resetsAt = now.AddHours(2).ToString("O", CultureInfo.InvariantCulture) },
                new { kind = "seven_day", percentUsed = weekly, resetsAt = now.AddDays(3).ToString("O", CultureInfo.InvariantCulture) }
            }
        }));
    }

    [Fact]
    public async Task A_refresh_records_the_reading_and_writes_the_forecast_for_the_plugin()
    {
        WriteRecord(43, 14);
        using var services = AppServices.Create(new AppSettings { ClaudeSource = "feed" },
            new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false, NoEndpoint,
            cacheDirectory: _dir.Combine("cache"));

        var summary = await services.Feed.GetSummaryAsync();

        Assert.Equal(43, summary.Usage.FiveHourUtilizationPercent);
        Assert.Equal([UsageWindowKinds.FiveHour, UsageWindowKinds.SevenDay], summary.Forecasts.Select(f => f.Kind));
        Assert.True(File.Exists(_dir.Combine("cache", "journal.jsonl")));
        using var bridge = JsonDocument.Parse(File.ReadAllText(_dir.Combine("feed", AppServices.ForecastFileName)));
        Assert.Equal(43, bridge.RootElement.GetProperty("windows")[0].GetProperty("percentUsed").GetDouble());
        // forecast.json sits in the feed folder but is not a session record.
        Assert.Single(services.ClaudeFeed!.Sessions);

        // One reading is no pace: the pill looks as before.
        var vm = WidgetViewModel.From(summary.Usage, summary.Session, new AppSettings(), DateTimeOffset.UtcNow,
            forecasts: summary.Forecasts);
        Assert.Null(vm.ForecastChipText);
        Assert.StartsWith("- Resets: ", vm.SecondaryText);
    }

    [Fact]
    public async Task No_reading_records_nothing()
    {
        using var services = AppServices.Create(new AppSettings { ClaudeSource = "feed" },
            new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false, NoEndpoint,
            cacheDirectory: _dir.Combine("cache"));

        var summary = await services.Feed.GetSummaryAsync();

        Assert.Empty(summary.Forecasts);
        Assert.False(File.Exists(_dir.Combine("cache", "journal.jsonl")));
        Assert.False(File.Exists(_dir.Combine("feed", AppServices.ForecastFileName)));
    }

    [Fact]
    public void The_forecast_settings_default_on_and_survive_a_round_trip()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":3,\"ClaudeSource\":\"feed\"}"); // a file from before the forecast

        var loaded = SettingsService.Load(path);
        Assert.True(loaded.ShowForecast);
        Assert.Equal("low", loaded.ForecastMinConfidence);

        SettingsService.Save(new AppSettings { ShowForecast = false, ForecastMinConfidence = "high" }, path);
        var back = SettingsService.Load(path);
        Assert.False(back.ShowForecast);
        Assert.Equal("high", back.ForecastMinConfidence);
        Assert.DoesNotContain("ForecastFloor", File.ReadAllText(path));
    }
}

public class ForecastFormatTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Short_times_follow_the_culture()
    {
        var at = new DateTimeOffset(2026, 9, 19, 10, 42, 0, TimeSpan.Zero); // 4:12 PM in IST

        Assert.Equal("4:12 PM", Format.ShortTime(at, Ist, Us));
        Assert.Equal("16:12", Format.ShortTime(at, Ist, Uk));
        Assert.Equal("4:12", Format.ShortTimeNoDesignator(at, Ist, Us));
        Assert.Equal("16:12", Format.ShortTimeNoDesignator(at, Ist, Uk));
    }

    [Fact]
    public void Reset_times_follow_a_24_hour_clock_too() =>
        Assert.Equal("Resets Fri 15:30", Format.ResetsAt(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), Now, Ist, Uk));

    [Theory]
    [InlineData("2026-09-19T11:50:00Z", "today 5:20 PM")]
    [InlineData("2026-09-20T04:00:00Z", "Sun 9:30 AM")]
    [InlineData("2026-09-28T10:00:00Z", "Sep 28, 3:30 PM")]
    public void Day_and_time(string atUtc, string expected) =>
        Assert.Equal(expected, Format.DayTime(DateTimeOffset.Parse(atUtc, CultureInfo.InvariantCulture), Now, Ist, Us));

    [Fact]
    public void The_last_day_before_a_reset_at_midnight_is_the_day_before() =>
        Assert.Equal("Nov 30", Format.LastDay(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
}
