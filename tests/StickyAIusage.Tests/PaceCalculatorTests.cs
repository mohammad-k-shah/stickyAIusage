using System.Globalization;
using System.Text.Json;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;

namespace StickyAIusage.Tests;

/// <summary>The pace arithmetic against the shared vectors in
/// <c>tests/vectors/pace</c> (the plugin's JavaScript version runs the same
/// files), plus the properties that must hold for any input.</summary>
public class PaceCalculatorTests
{
    private static readonly string VectorDirectory = Path.Combine(AppContext.BaseDirectory, "vectors", "pace");

    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(VectorDirectory, "*.json").OrderBy(f => f))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Fact]
    public void There_are_at_least_twelve_vectors() =>
        Assert.True(Directory.GetFiles(VectorDirectory, "*.json").Length >= 12);

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Vector(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorDirectory, name + ".json")));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        var now = DateTimeOffset.Parse(root.GetProperty("now").GetString()!, CultureInfo.InvariantCulture);
        var kind = root.GetProperty("kind").GetString()!;
        var resetsAt = now.AddMinutes(root.GetProperty("resetsInMinutes").GetDouble());
        var length = TimeSpan.FromMinutes(root.GetProperty("lengthMinutes").GetDouble());
        var samples = root.GetProperty("samples").EnumerateArray().Select(s => new UsageSample("claude", kind,
                s.TryGetProperty("resetsInMinutes", out var own) ? now.AddMinutes(own.GetDouble()) : resetsAt,
                s.GetProperty("percent").GetDouble(), now.AddMinutes(s.GetProperty("minutes").GetDouble())))
            .ToList();
        var tolerance = root.GetProperty("tolerance");
        var expected = root.GetProperty("expected");

        var forecast = PaceCalculator.Compute(kind, samples, now, resetsAt, length,
            root.GetProperty("wholePointsOnly").GetBoolean());

        Assert.Equal(expected.GetProperty("percentNow").GetDouble(), forecast.PercentNow, 1);
        Assert.Equal(expected.GetProperty("rateKnown").GetBoolean(), forecast.RatePerHour is not null);
        if (Number(expected, "ratePerHour") is { } rate)
            Assert.InRange(forecast.RatePerHour!.Value, rate * (1 - Relative()), rate * (1 + Relative()));
        Assert.Equal(expected.GetProperty("confidence").GetString(), forecast.Confidence.ToString().ToLowerInvariant());

        var hitsIn = Number(expected, "hitsLimitInMinutes");
        Assert.Equal(hitsIn is not null, forecast.HitsLimitAt is not null);
        if (hitsIn is { } minutes)
            Assert.InRange((forecast.HitsLimitAt!.Value - now).TotalMinutes,
                minutes - tolerance.GetProperty("hitsLimitMinutes").GetDouble(), minutes + tolerance.GetProperty("hitsLimitMinutes").GetDouble());

        var points = tolerance.GetProperty("percent").GetDouble();
        if (expected.TryGetProperty("projectedAtReset", out _))
            AssertNear(Number(expected, "projectedAtReset"), forecast.ProjectedAtReset, points);
        AssertNear(Number(expected, "aheadOfEvenPace"), forecast.AheadOfEvenPace, points);

        double Relative() => tolerance.GetProperty("ratePerHourRelative").GetDouble();
    }

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static void AssertNear(double? expected, double? actual, double tolerance)
    {
        Assert.Equal(expected is not null, actual is not null);
        if (expected is { } e)
            Assert.InRange(actual!.Value, e - tolerance, e + tolerance);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);

    private static List<UsageSample> Steady(DateTimeOffset resetsAt, double start, double perStep, int count, int stepMinutes = 5,
        string kind = UsageWindowKinds.FiveHour) =>
        Enumerable.Range(0, count).Select(i => new UsageSample("claude", kind, resetsAt, start + perStep * i,
            Now.AddMinutes(-stepMinutes * (count - 1 - i)))).ToList();

    [Fact]
    public void Thirty_minutes_of_steady_burn_gives_the_limit_time_within_ten_minutes()
    {
        // 2 points a minute from 30%: 100% at 35 minutes from the first
        // reading, so 5 minutes from now.
        var reset = Now.AddHours(2);
        var samples = Enumerable.Range(0, 31).Select(i => new UsageSample("claude", UsageWindowKinds.FiveHour, reset,
            30 + 2 * i, Now.AddMinutes(i - 30))).ToList();

        var forecast = PaceCalculator.Compute(UsageWindowKinds.FiveHour, samples, Now, reset, FiveHours);

        Assert.Equal(ForecastConfidence.High, forecast.Confidence);
        Assert.InRange((forecast.HitsLimitAt!.Value - Now.AddMinutes(5)).Duration().TotalMinutes, 0, 10);
    }

    [Fact]
    public void Samples_from_the_future_are_ignored()
    {
        var reset = Now.AddHours(2);
        var samples = Steady(reset, 40, 5, 7);
        samples.Add(new UsageSample("claude", UsageWindowKinds.FiveHour, reset, 99, Now.AddMinutes(10)));

        var forecast = PaceCalculator.Compute(UsageWindowKinds.FiveHour, samples, Now, reset, FiveHours);

        Assert.Equal(70, forecast.PercentNow);
    }

    [Fact]
    public void A_window_whose_reset_passed_has_no_forecast()
    {
        var reset = Now.AddMinutes(-1);
        var forecast = PaceCalculator.Compute(UsageWindowKinds.FiveHour, Steady(reset, 40, 5, 7), Now, reset, FiveHours);

        Assert.Equal(ForecastConfidence.None, forecast.Confidence);
        Assert.Equal(0, forecast.PercentNow);
        Assert.Null(forecast.HitsLimitAt);
        Assert.Null(forecast.ProjectedAtReset);
    }

    [Fact]
    public void No_samples_and_no_reset_time_give_no_forecast()
    {
        Assert.Equal(ForecastConfidence.None,
            PaceCalculator.Compute(UsageWindowKinds.FiveHour, [], Now, Now.AddHours(1), FiveHours).Confidence);
        var unknownReset = PaceCalculator.Compute(UsageWindowKinds.FiveHour,
            Steady(Now.AddHours(1), 40, 5, 7).Select(s => s with { ResetsAt = null }).ToList(), Now, null, FiveHours);
        Assert.Null(unknownReset.HitsLimitAt);
        Assert.Null(unknownReset.ProjectedAtReset);
    }

    [Fact]
    public void A_falling_reading_never_gives_a_negative_rate()
    {
        var reset = Now.AddHours(2);
        // Rises by a point twice, then falls (a correction): slope < 0 → 0.
        var samples = new List<UsageSample>
        {
            new("claude", UsageWindowKinds.FiveHour, reset, 50, Now.AddMinutes(-30)),
            new("claude", UsageWindowKinds.FiveHour, reset, 60, Now.AddMinutes(-25)),
            new("claude", UsageWindowKinds.FiveHour, reset, 52, Now.AddMinutes(-5)),
            new("claude", UsageWindowKinds.FiveHour, reset, 51, Now)
        };

        var forecast = PaceCalculator.Compute(UsageWindowKinds.FiveHour, samples, Now, reset, FiveHours);

        Assert.True(forecast.RatePerHour is null or >= 0);
        Assert.Null(forecast.HitsLimitAt);
    }

    [Theory]
    [InlineData(UsageWindowKinds.FiveHour, null, 30)]
    [InlineData(UsageWindowKinds.SevenDay, null, 24 * 60)]
    [InlineData(UsageWindowKinds.SevenDayOpus, null, 24 * 60)]
    [InlineData(UsageWindowKinds.MonthSpend, null, 7 * 24 * 60)]
    [InlineData(UsageWindowKinds.SpendLimit, null, 7 * 24 * 60)]
    [InlineData("seven_day_cowork", 7 * 24 * 60.0, 7 * 24 * 6)]
    [InlineData("something_new", null, 30)]
    public void Lookback_by_kind(string kind, double? lengthMinutes, double expectedMinutes) =>
        Assert.Equal(expectedMinutes, PaceCalculator.Lookback(kind, lengthMinutes is { } l ? TimeSpan.FromMinutes(l) : null).TotalMinutes);

    [Fact]
    public void The_basis_names_the_lookback()
    {
        var reset = Now.AddHours(2);
        Assert.Equal("the last 30 minutes", PaceCalculator.Compute(UsageWindowKinds.FiveHour, [], Now, reset, FiveHours).Basis);
        Assert.Equal("the last 24 hours", PaceCalculator.Compute(UsageWindowKinds.SevenDay, [], Now, reset, TimeSpan.FromDays(7)).Basis);
        Assert.Equal("the last 7 days", PaceCalculator.Compute(UsageWindowKinds.MonthSpend, [], Now, reset, null).Basis);
    }

    /// <summary>For any samples: a limit time is never in the past and never
    /// at or after the reset, and there is none without a rise.</summary>
    [Fact]
    public void Properties_hold_for_random_traces()
    {
        var random = new Random(12);
        for (var trace = 0; trace < 2000; trace++)
        {
            var reset = Now.AddMinutes(random.Next(1, 300));
            var count = random.Next(0, 15);
            var percent = random.NextDouble() * 100;
            var samples = new List<UsageSample>();
            for (var i = 0; i < count; i++)
            {
                percent = Math.Clamp(percent + (random.NextDouble() < 0.3 ? 0 : random.NextDouble() * 8 - 1), 0, 120);
                samples.Add(new UsageSample("claude", UsageWindowKinds.FiveHour, reset, Math.Round(percent, 1),
                    Now.AddMinutes(-random.Next(0, 60))));
            }

            var forecast = PaceCalculator.Compute(UsageWindowKinds.FiveHour, samples, Now, reset, FiveHours);

            if (forecast.HitsLimitAt is { } at)
            {
                Assert.True(at >= Now, $"trace {trace}: limit time in the past");
                Assert.True(at < reset, $"trace {trace}: limit time after the reset");
                Assert.True(forecast.IsActive);
                Assert.True(forecast.PercentNow < 100);
            }
            Assert.True(forecast.RatePerHour is null or >= 0);
            var ordered = samples.OrderBy(s => s.ObservedAt).ToList();
            var rose = ordered.Zip(ordered.Skip(1)).Any(p => p.Second.PercentUsed > p.First.PercentUsed);
            if (!rose)
            {
                Assert.Null(forecast.HitsLimitAt);
                Assert.False(forecast.IsActive);
            }
        }
    }
}
