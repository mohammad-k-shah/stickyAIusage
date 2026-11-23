using StickyAIusage.Models;

namespace StickyAIusage.Forecasting;

/// <summary>How much a forecast can be trusted. None: there is no forecast.</summary>
public enum ForecastConfidence { None, Low, High }

/// <summary>
/// Where one usage window is heading. <paramref name="RatePerHour"/> is the
/// recent burn in points per hour (null when there is too little to go on);
/// <paramref name="HitsLimitAt"/> is when 100% comes at that rate, only when it
/// comes before the reset; <paramref name="ProjectedAtReset"/> is where the
/// window ends (may pass 100); <paramref name="AheadOfEvenPace"/> is how many
/// points above (+) or below (−) a steady spread over the whole window the
/// usage is. <paramref name="Basis"/> says what the rate was measured over
/// ("the last 30 minutes").
/// </summary>
public sealed record PaceForecast(string Kind, double PercentNow, double? RatePerHour,
    DateTimeOffset? HitsLimitAt, double? ProjectedAtReset, double? AheadOfEvenPace,
    ForecastConfidence Confidence, string Basis)
{
    /// <summary>The reset of the window this forecast is for.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>True when usage rose recently (within the kind's activity span).</summary>
    public bool IsActive { get; init; }

    public bool LimitReached => PercentNow >= 100;

    /// <summary>No forecast: the window is idle, too new or already over.</summary>
    public static PaceForecast None(string kind, double percentNow, DateTimeOffset? resetsAt, string basis) =>
        new(kind, percentNow, null, null, null, null, ForecastConfidence.None, basis) { ResetsAt = resetsAt };
}

/// <summary>
/// The pace arithmetic, pure and deterministic (all instants in UTC, time
/// injected). The same definition is followed by the plugin's JavaScript
/// version; both are pinned by the shared vectors in <c>tests/vectors/pace</c>.
/// 1. Only the samples of the current window count (same reset within two minutes).
/// 2. The rate is measured over a lookback by kind: 5-hour 30 minutes, weekly
///    24 hours, a spend cap 7 days, anything else a tenth of its length.
/// 3. Rate = least-squares slope of the samples inside the lookback, never
///    below zero; it needs 3 samples spanning a third of the lookback and a
///    rise of 1 point (2 for whole-point sources), else it is unknown.
/// 4. Active = the last rise is recent (10 minutes, 6 hours, 2 days).
/// 5. The limit time is only given while active, and only before the reset;
///    never in the past. It runs from the newest sample, whose percentage is
///    as of its own time.
/// 6. The projection at the reset uses the recent rate while active and the
///    window's average so far otherwise.
/// 7. Even pace = the share of the window that has passed.
/// 8. Confidence: High with 6 samples spanning 80% of the lookback and R² ≥ 0.8.
/// </summary>
public static class PaceCalculator
{
    /// <summary>Two readings of one window may report its reset a little apart.</summary>
    public static readonly TimeSpan SameResetTolerance = TimeSpan.FromMinutes(2);

    public static PaceForecast Compute(string kind, IReadOnlyList<UsageSample> samples,
        DateTimeOffset now, DateTimeOffset? resetsAt, TimeSpan? length, bool wholePointsOnly = false)
    {
        var lookback = Lookback(kind, length);
        var basis = "the last " + Describe(lookback);

        var current = samples
            .Where(s => s.ObservedAt <= now && SameReset(s.ResetsAt, resetsAt))
            .OrderBy(s => s.ObservedAt)
            .ToList();
        // A window whose reset has passed has started over: 0%, nothing to forecast.
        if (resetsAt is { } over && over <= now)
            return PaceForecast.None(kind, 0, resetsAt, basis);
        if (current.Count == 0)
            return PaceForecast.None(kind, 0, resetsAt, basis);

        var newest = current[^1];
        var percentNow = newest.PercentUsed;

        var recent = current.Where(s => s.ObservedAt >= now - lookback).ToList();
        double? rate = null;
        double rSquared = 0;
        if (recent.Count >= 3 &&
            recent[^1].ObservedAt - recent[0].ObservedAt >= lookback / 3 &&
            recent[^1].PercentUsed - recent[0].PercentUsed >= (wholePointsOnly ? 2 : 1))
        {
            var (slope, r2) = LeastSquares(recent);
            rate = Math.Max(0, slope);
            rSquared = r2;
        }

        var active = LastRise(current) is { } rise && rise >= now - ActiveFor(kind, lookback);

        DateTimeOffset? hitsLimitAt = null;
        if (active && rate is > 0 && percentNow < 100 && resetsAt is { } reset)
        {
            var at = newest.ObservedAt + TimeSpan.FromHours((100 - percentNow) / rate.Value);
            if (at < now)
                at = now;
            if (at < reset)
                hitsLimitAt = at;
        }

        double? projected = null, ahead = null;
        if (resetsAt is { } end)
        {
            if (active && rate is { } r)
                projected = percentNow + r * (end - newest.ObservedAt).TotalHours;
            if (length is { } span && span > TimeSpan.Zero)
            {
                var elapsed = span - (end - now);
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed > span) elapsed = span;
                ahead = percentNow - 100 * (elapsed / span);
                if (projected is null && elapsed > TimeSpan.Zero)
                    projected = percentNow + percentNow / elapsed.TotalHours * (end - now).TotalHours;
            }
        }

        var confidence = rate is null ? ForecastConfidence.None
            : recent.Count >= 6 && recent[^1].ObservedAt - recent[0].ObservedAt >= lookback * 0.8 && rSquared >= 0.8
                ? ForecastConfidence.High
                : ForecastConfidence.Low;

        return new PaceForecast(kind, percentNow, rate, hitsLimitAt, projected, ahead, confidence, basis)
        {
            ResetsAt = resetsAt,
            IsActive = active
        };
    }

    /// <summary>What the rate is measured over, by kind.</summary>
    public static TimeSpan Lookback(string kind, TimeSpan? length) => kind switch
    {
        UsageWindowKinds.FiveHour => TimeSpan.FromMinutes(30),
        UsageWindowKinds.SevenDay or UsageWindowKinds.SevenDayOpus or UsageWindowKinds.SevenDaySonnet => TimeSpan.FromHours(24),
        UsageWindowKinds.MonthSpend or UsageWindowKinds.SpendLimit => TimeSpan.FromDays(7),
        _ => length is { } l && l > TimeSpan.Zero ? l / 10 : TimeSpan.FromMinutes(30)
    };

    /// <summary>How recent the last rise must be for the window to count as in use.</summary>
    public static TimeSpan ActiveFor(string kind, TimeSpan lookback) => kind switch
    {
        UsageWindowKinds.FiveHour => TimeSpan.FromMinutes(10),
        UsageWindowKinds.SevenDay or UsageWindowKinds.SevenDayOpus or UsageWindowKinds.SevenDaySonnet => TimeSpan.FromHours(6),
        UsageWindowKinds.MonthSpend or UsageWindowKinds.SpendLimit => TimeSpan.FromDays(2),
        _ => lookback / 3
    };

    public static bool SameReset(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b is null : b is not null && (a.Value - b.Value).Duration() <= SameResetTolerance;

    /// <summary>When the percentage last went up, or null if it never did.</summary>
    private static DateTimeOffset? LastRise(List<UsageSample> ordered)
    {
        for (var i = ordered.Count - 1; i > 0; i--)
            if (ordered[i].PercentUsed > ordered[i - 1].PercentUsed)
                return ordered[i].ObservedAt;
        return null;
    }

    /// <summary>Slope in points per hour and R² of a straight line through the samples.</summary>
    private static (double Slope, double RSquared) LeastSquares(List<UsageSample> points)
    {
        var origin = points[0].ObservedAt;
        var n = points.Count;
        double sx = 0, sy = 0;
        foreach (var p in points)
        {
            sx += (p.ObservedAt - origin).TotalHours;
            sy += p.PercentUsed;
        }
        var mx = sx / n;
        var my = sy / n;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in points)
        {
            var dx = (p.ObservedAt - origin).TotalHours - mx;
            var dy = p.PercentUsed - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        if (sxx <= 0)
            return (0, 0);
        var slope = sxy / sxx;
        var r2 = syy <= 0 ? 0 : sxy * sxy / (sxx * syy);
        return (slope, r2);
    }

    private static string Describe(TimeSpan span) =>
        span.TotalDays >= 1 && span.TotalDays % 1 == 0 ? span.TotalDays == 1 ? "24 hours" : $"{span.TotalDays:0} days"
        : span.TotalHours >= 1 && span.TotalHours % 1 == 0 ? span.TotalHours == 1 ? "hour" : $"{span.TotalHours:0} hours"
        : $"{Math.Round(span.TotalMinutes):0} minutes";
}
