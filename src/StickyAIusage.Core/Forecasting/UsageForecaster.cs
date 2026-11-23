using System.Globalization;
using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Forecasting;

/// <summary>
/// Turns each usage reading into pace forecasts: records it in the
/// <see cref="UsageJournal"/>, computes a <see cref="PaceForecast"/> per window
/// (<see cref="PaceCalculator"/>), and writes them to <c>feed/forecast.json</c>
/// for the Claude Code plugin. Two guards against jittery text: the forecasts
/// are worked out again only when a new sample arrived or a minute passed, and
/// a limit time that moved by two minutes or less keeps its old value. Called
/// from one refresh at a time; never throws.
/// </summary>
public sealed class UsageForecaster : IPaceForecaster
{
    public static readonly TimeSpan RecomputeEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan KeepShownTimeWithin = TimeSpan.FromMinutes(2);

    /// <summary>The bridge is rewritten at least this often while readings come,
    /// so a reader can tell a current file from an old one by its <c>updatedAt</c>.</summary>
    public static readonly TimeSpan BridgeEvery = TimeSpan.FromMinutes(1);

    private readonly UsageJournal _journal;
    private readonly string? _bridgePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, Shown> _shown = new(StringComparer.Ordinal);
    private string? _lastBridgeContent;
    private DateTimeOffset _lastBridgeWrite = DateTimeOffset.MinValue;
    private bool _bridgeFailureLogged;

    /// <param name="bridgePath">Where <c>forecast.json</c> goes; null: not written.</param>
    /// <param name="zone">The local zone, for the start of the next month
    /// (a monthly cap whose reset time isn't reported).</param>
    public UsageForecaster(UsageJournal journal, string? bridgePath, Func<DateTimeOffset> clock, TimeZoneInfo? zone = null)
    {
        _journal = journal;
        _bridgePath = bridgePath;
        _clock = clock;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>One forecast per window of the reading, in the reading's order.
    /// Several providers may be updated in turn; each keeps its own state.</summary>
    public IReadOnlyList<PaceForecast> Update(ProviderUsage usage)
    {
        try
        {
            var now = _clock();
            var added = _journal.Record(usage, w => ResetFor(w, now));
            var kinds = string.Join(",", usage.Windows.Select(w => w.Kind));
            var last = _shown.GetValueOrDefault(usage.ProviderId) ?? Shown.None;
            if (!added && kinds == last.Kinds && now - last.Computed < RecomputeEvery)
                return last.Forecasts;

            var previous = last.Forecasts;
            var forecasts = usage.Windows.Select(w =>
            {
                var reset = ResetFor(w, now);
                var forecast = PaceCalculator.Compute(w.Kind, _journal.Samples(usage.ProviderId, w.Kind), now, reset,
                    LengthOf(w, reset), usage.WholePointsOnly);
                return Steady(forecast, previous, now);
            }).ToList();

            _shown[usage.ProviderId] = new Shown(forecasts, now, kinds);
            // The plugin runs inside Claude Code: it reads Claude's forecast only.
            if (usage.ProviderId == BridgeProvider)
                WriteBridge(forecasts, now);
            return forecasts;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageForecaster.Update", ex);
            return [];
        }
    }

    /// <summary>The provider whose forecast goes to <c>forecast.json</c>.</summary>
    public const string BridgeProvider = "claude";

    /// <summary>What was last worked out for one provider: each provider keeps its
    /// own forecasts, recompute minute and steadiness.</summary>
    private sealed record Shown(IReadOnlyList<PaceForecast> Forecasts, DateTimeOffset Computed, string Kinds)
    {
        public static readonly Shown None = new([], DateTimeOffset.MinValue, "");
    }

    /// <summary>The window's reset; for a monthly cap the endpoint doesn't
    /// date, the start of next month (local time) is assumed.</summary>
    private DateTimeOffset? ResetFor(UsageWindow window, DateTimeOffset now) => ResetOf(window, now, _zone);

    /// <summary>The window's reported reset; for a monthly cap without one, the
    /// start of next month in <paramref name="zone"/>. Shared with the alerts,
    /// so both name a monthly window's series the same way.</summary>
    public static DateTimeOffset? ResetOf(UsageWindow window, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (window.ResetsAt is { } reset)
            return reset;
        if (window.Kind != UsageWindowKinds.MonthSpend)
            return null;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var next = new DateTime(local.Year, local.Month, 1).AddMonths(1);
        return new DateTimeOffset(next, zone.GetUtcOffset(next));
    }

    /// <summary>The window's length; a monthly cap's is its calendar month.</summary>
    private static TimeSpan? LengthOf(UsageWindow window, DateTimeOffset? reset) =>
        window.Length ?? (window.Kind == UsageWindowKinds.MonthSpend && reset is { } r ? r - r.AddMonths(-1) : null);

    /// <summary>A limit time that moved by two minutes or less keeps the
    /// value already shown, so the pill doesn't flicker between 4:12 and 4:13.</summary>
    private static PaceForecast Steady(PaceForecast forecast, IReadOnlyList<PaceForecast> previous, DateTimeOffset now)
    {
        if (forecast.HitsLimitAt is not { } next)
            return forecast;
        var shown = previous.FirstOrDefault(p => p.Kind == forecast.Kind && PaceCalculator.SameReset(p.ResetsAt, forecast.ResetsAt));
        return shown?.HitsLimitAt is { } old && old >= now && (old - next).Duration() <= KeepShownTimeWithin
            ? forecast with { HitsLimitAt = old }
            : forecast;
    }

    private void WriteBridge(IReadOnlyList<PaceForecast> forecasts, DateTimeOffset now)
    {
        if (_bridgePath is null || forecasts.Count == 0)
            return;
        var content = BridgeWindows(forecasts);
        if (content == _lastBridgeContent && now - _lastBridgeWrite < BridgeEvery)
            return;
        try
        {
            AtomicFile.WriteAllText(_bridgePath, BridgeJson(content, now));
            _lastBridgeContent = content;
            _lastBridgeWrite = now;
        }
        catch (Exception ex)
        {
            if (!_bridgeFailureLogged)
                DiagnosticLog.WriteError("UsageForecaster.WriteBridge", ex);
            _bridgeFailureLogged = true;
        }
    }

    /// <summary>The windows part of <c>forecast.json</c>, compared to skip unchanged writes.</summary>
    internal static string BridgeWindows(IReadOnlyList<PaceForecast> forecasts) =>
        JsonSerializer.Serialize(forecasts.Select(f => new Dictionary<string, object?>
        {
            ["kind"] = f.Kind,
            ["percentUsed"] = Math.Round(f.PercentNow, 1),
            ["resetsAt"] = Iso(f.ResetsAt),
            ["hitsLimitAt"] = Iso(f.HitsLimitAt),
            ["projectedAtReset"] = f.ProjectedAtReset is { } p ? Math.Round(p, 1) : null,
            ["confidence"] = f.Confidence.ToString().ToLowerInvariant()
        }));

    /// <summary><c>{ "schema": 1, "updatedAt": …, "windows": [ … ] }</c>.</summary>
    internal static string BridgeJson(string windows, DateTimeOffset now) =>
        $"{{\"schema\":1,\"updatedAt\":\"{Iso(now)}\",\"windows\":{windows}}}\n";

    private static string? Iso(DateTimeOffset? at) =>
        at?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
