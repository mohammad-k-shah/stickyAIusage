using System.Text.Json;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class UsageForecasterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private DateTimeOffset _now = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 11, 17, 16, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private string BridgePath => _dir.Combine("feed", AppServices.ForecastFileName);

    private UsageForecaster Create(string? bridge = "default") =>
        new(new UsageJournal(_dir.Combine("cache", "journal.jsonl"), () => _now),
            bridge == "default" ? BridgePath : bridge, () => _now, TimeZoneInfo.Utc);

    private ProviderUsage Plan(double fiveHour, double weekly = 20) => new UsageSnapshot
    {
        FiveHourUtilizationPercent = fiveHour,
        FiveHourResetsAt = Reset,
        WeeklyUtilizationPercent = weekly,
        WeeklyResetsAt = Reset.AddDays(3),
        FetchedAt = _now
    }.ToProviderUsage();

    /// <summary>A reading every 5 minutes for 30 minutes, 5 points apart:
    /// 60 points an hour, 70% at the end, limit 30 minutes later.</summary>
    private IReadOnlyList<PaceForecast> Burn(UsageForecaster forecaster, double from = 40)
    {
        IReadOnlyList<PaceForecast> last = [];
        for (var i = 0; i <= 6; i++)
        {
            last = forecaster.Update(Plan(from + 5 * i));
            if (i < 6)
                _now = _now.AddMinutes(5);
        }
        return last;
    }

    [Fact]
    public void A_steady_burn_forecasts_the_limit_for_the_five_hour_window()
    {
        var forecasts = Burn(Create());

        var fiveHour = forecasts.Single(f => f.Kind == UsageWindowKinds.FiveHour);
        Assert.Equal(ForecastConfidence.High, fiveHour.Confidence);
        Assert.Equal(_now.AddMinutes(30), fiveHour.HitsLimitAt);
        Assert.Equal(Reset, fiveHour.ResetsAt);
        // The weekly window didn't move: no rate.
        Assert.Equal(ForecastConfidence.None, forecasts.Single(f => f.Kind == UsageWindowKinds.SevenDay).Confidence);
    }

    [Fact]
    public void Forecasts_are_worked_out_again_only_on_a_new_sample_or_after_a_minute()
    {
        var forecaster = Create();
        var first = Burn(forecaster);

        _now = _now.AddSeconds(15);
        Assert.Same(first, forecaster.Update(Plan(70))); // same observation, 15 s later
        _now = _now.AddSeconds(50);
        Assert.NotSame(first, forecaster.Update(Plan(70)));
    }

    [Fact]
    public void A_limit_time_that_moves_by_two_minutes_or_less_keeps_its_value()
    {
        var forecaster = Create();
        var shown = Burn(forecaster).Single(f => f.Kind == UsageWindowKinds.FiveHour).HitsLimitAt;

        // A minute later, the same 60/h rate but a reading 1 point lower:
        // the computed time moves by a minute; the shown one stays.
        _now = _now.AddMinutes(1);
        var next = forecaster.Update(Plan(70.5)).Single(f => f.Kind == UsageWindowKinds.FiveHour);
        Assert.Equal(shown, next.HitsLimitAt);

        // A big jump moves it.
        _now = _now.AddMinutes(1);
        var jumped = forecaster.Update(Plan(90)).Single(f => f.Kind == UsageWindowKinds.FiveHour);
        Assert.NotEqual(shown, jumped.HitsLimitAt);
    }

    [Fact]
    public void The_bridge_file_matches_the_forecast()
    {
        var forecasts = Burn(Create());

        using var doc = JsonDocument.Parse(File.ReadAllText(BridgePath));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(_now, root.GetProperty("updatedAt").GetDateTimeOffset());
        var five = root.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("kind").GetString() == "five_hour");
        Assert.Equal(70, five.GetProperty("percentUsed").GetDouble());
        Assert.Equal(Reset, five.GetProperty("resetsAt").GetDateTimeOffset());
        Assert.Equal(forecasts.Single(f => f.Kind == "five_hour").HitsLimitAt, five.GetProperty("hitsLimitAt").GetDateTimeOffset());
        Assert.Equal("high", five.GetProperty("confidence").GetString());
        var week = root.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("kind").GetString() == "seven_day");
        Assert.Equal(JsonValueKind.Null, week.GetProperty("hitsLimitAt").ValueKind);
        Assert.Equal("none", week.GetProperty("confidence").GetString());
        // Written atomically: no temp file left beside it.
        Assert.Equal([BridgePath], Directory.GetFiles(Path.GetDirectoryName(BridgePath)!));
    }

    [Fact]
    public void An_unchanged_bridge_is_rewritten_once_a_minute()
    {
        var forecaster = Create();
        forecaster.Update(Plan(40));
        var first = File.ReadAllText(BridgePath);

        _now = _now.AddSeconds(61); // recomputed (a minute passed), content the same
        forecaster.Update(Plan(40));
        var second = File.ReadAllText(BridgePath);

        Assert.NotEqual(first, second); // updatedAt moved
        Assert.Contains("\"updatedAt\":\"2026-11-17T14:01:01.000Z\"", second);
    }

    [Fact]
    public void No_bridge_path_writes_nothing_and_a_failed_write_never_throws()
    {
        Burn(Create(bridge: null));
        Assert.False(File.Exists(BridgePath));

        File.WriteAllText(_dir.Combine("blocked"), "");
        var forecasts = Burn(Create(_dir.Combine("blocked", "forecast.json")));
        Assert.NotEmpty(forecasts);
    }

    [Fact]
    public void A_monthly_cap_without_a_reset_time_runs_to_the_start_of_next_month()
    {
        var forecaster = Create();
        var cap = new UsageSnapshot
        {
            HasMonthlySpend = true, MonthlySpendUsedUsd = 100, MonthlySpendLimitUsd = 200, MonthlySpendPercent = 50, FetchedAt = _now
        }.ToProviderUsage();

        var forecast = forecaster.Update(cap).Single(f => f.Kind == UsageWindowKinds.MonthSpend);

        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), forecast.ResetsAt);
        // 16 Nov 14:00 of 30 days have passed: an even pace is 54.7%.
        Assert.Equal(50 - 100 * (16 + 14 / 24.0) / 30, forecast.AheadOfEvenPace!.Value, 3);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            new UsageJournal(_dir.Combine("cache", "journal.jsonl")).Samples("claude", UsageWindowKinds.MonthSpend).Single().ResetsAt);
    }

    [Fact]
    public void A_reset_mid_run_starts_a_new_series()
    {
        var forecaster = Create();
        Burn(forecaster, from: 60); // 90% at 14:30, reset at 16:00
        _now = Reset.AddMinutes(1);
        var newWindow = new UsageSnapshot
        {
            FiveHourUtilizationPercent = 2, FiveHourResetsAt = Reset.AddHours(5), FetchedAt = _now
        }.ToProviderUsage();

        var forecast = forecaster.Update(newWindow).Single();

        Assert.Equal(2, forecast.PercentNow);
        Assert.Equal(ForecastConfidence.None, forecast.Confidence); // one sample in the new window
    }
}
