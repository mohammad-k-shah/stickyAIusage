using System.Globalization;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Alerts;

/// <summary>What one plan decided: the alerts to show, the ones quiet hours
/// or the mute swallowed (already marked as told), and the state to keep.</summary>
public sealed record AlertPlan(IReadOnlyList<Alert> Alerts, IReadOnlyList<Alert> Dropped, AlertState State);

/// <summary>
/// Decides which notifications a reading deserves. Pure: readings, forecasts,
/// settings, the state of the last plan and the time go in; alerts and the
/// next state come out. Rules:
/// <list type="bullet">
/// <item><b>Threshold</b>: a window at or above the orange or red threshold, once per window (reset) and threshold.</item>
/// <item><b>ForecastHit</b>: the forecast puts the limit within the lead time (30 minutes) and before the reset; once
/// per window, again only after the forecast has said otherwise for 15 minutes.</item>
/// <item><b>LimitReached</b>: a window at 100%, once per window.</item>
/// <item><b>WindowReset</b>: a window that reached the orange threshold has reset ("you're back"); once per reset,
/// given with the first reading of the new window or a minute after the reset, whichever is first, and not at all
/// when the reset is more than an hour old by then.</item>
/// <item><b>WeeklyRunway</b>: the weekly forecast runs out before the weekly reset; at most once a day.</item>
/// <item><b>SpendPace</b>: a spend cap's projection passes the cap; at most once a day.</item>
/// <item><b>NeedsYou</b> / <b>AgentDone</b>: from the session lights (<see cref="PlanSessions"/>).</item>
/// </list>
/// A window gives at most one alert per reading, the most urgent one due
/// (limit, forecast, runway, spend pace, red, orange); the others that hold
/// count as told, so a jump from 70% to 100% is one notification, not three.
/// Quiet hours and the mute drop alerts: they are marked as told and not shown.
/// </summary>
public static class AlertPlanner
{
    public static readonly TimeSpan ForecastRearmAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ResetGrace = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ResetTooOld = TimeSpan.FromHours(1);
    public static readonly TimeSpan KeepAfterReset = TimeSpan.FromDays(1);
    public static readonly TimeSpan KeepWithoutReset = TimeSpan.FromDays(35);

    /// <summary>A window without a reported reset that reads this many points
    /// below what an alert was about has evidently started over.</summary>
    public const double StartedOverDrop = 10;

    private sealed record Option(AlertKind Kind, string Detail, string Title, string Body, UsageLevel Level);

    public static AlertPlan Plan(IReadOnlyList<AlertInput> inputs, AlertSettings settings, AlertState state,
        DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null,
        IReadOnlyList<SessionActivity>? sessions = null)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var told = new Dictionary<string, ToldAlert>(StringComparer.Ordinal);
        foreach (var entry in state.Told)
            told[entry.Key] = entry;
        var peaks = state.Peaks.ToList();
        var due = new List<Alert>();
        var named = inputs.Select(i => i.Usage.ProviderId).Distinct().Count() > 1;
        var today = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var input in inputs)
        {
            var usage = input.Usage;
            var provider = usage.ProviderId;
            string Title(string text) => named ? $"{usage.DisplayName}: {text}" : text;

            // Resets of windows that had been busy.
            foreach (var peak in peaks.Where(p => p.Provider == provider).ToList())
            {
                var newer = usage.Windows.Any(w => w.Kind == peak.Kind && w.ResetsAt is { } r &&
                                                   r > peak.ResetsAt && !PaceCalculator.SameReset(r, peak.ResetsAt));
                var ended = peak.ResetsAt <= now - ResetGrace || newer && peak.ResetsAt <= now + PaceCalculator.SameResetTolerance;
                if (!ended)
                {
                    if (newer)
                        peaks.Remove(peak); // the reset moved: a new series, nothing to announce
                    continue;
                }
                peaks.Remove(peak);
                if (!(settings.Enabled && settings.OnReset) || peak.Peak < settings.OrangePercent || now - peak.ResetsAt > ResetTooOld)
                    continue;
                var key = KeyFor(told, provider, peak.Kind, peak.ResetsAt, AlertKind.WindowReset, "");
                if (told.ContainsKey(key))
                    continue;
                told[key] = new ToldAlert(key, provider, peak.Kind, AlertKind.WindowReset, "", peak.ResetsAt, peak.Peak, now);
                due.Add(new Alert(key, AlertKind.WindowReset, Title("You're back"),
                    $"The {Noun(peak.Kind, usage)} has reset.", UsageLevel.Green));
            }

            if (usage.Health is UsageHealth.NeedsSignIn or UsageHealth.Unavailable)
                continue;

            foreach (var window in usage.Windows)
            {
                // A window whose reset has passed has started over: 0%.
                if (window.ResetsAt is { } passed && passed <= now)
                    continue;
                var forecastReset = UsageForecaster.ResetOf(window, now, zone);
                var seriesReset = forecastReset ?? SiblingReset(usage, window);
                if (window.ResetsAt is { } reported)
                    TrackPeak(peaks, provider, window.Kind, reported, window.PercentUsed);

                var forecast = input.Forecasts.FirstOrDefault(f => f.Kind == window.Kind && PaceCalculator.SameReset(f.ResetsAt, forecastReset));
                var trusted = forecast is not null && forecast.Confidence >= settings.ForecastFloor && forecast.Confidence > ForecastConfidence.None;
                var percent = window.PercentUsed;
                var hitSoon = trusted && percent < 100 && forecast!.HitsLimitAt is { } h && h - now <= settings.ForecastLead;

                // The forecast alert comes back once the forecast has said otherwise for a while.
                var forecastKey = KeyFor(told, provider, window.Kind, seriesReset, AlertKind.ForecastHit, "");
                if (told.TryGetValue(forecastKey, out var shown))
                {
                    if (hitSoon)
                        told[forecastKey] = shown with { ClearSince = null };
                    else if (shown.ClearSince is { } clear && now - clear >= ForecastRearmAfter)
                        told.Remove(forecastKey);
                    else if (shown.ClearSince is null)
                        told[forecastKey] = shown with { ClearSince = now };
                }

                var options = Options(window, usage, forecast, trusted, hitSoon, settings, now, zone, culture, today);
                if (options.Count == 0)
                    continue;
                var keys = options.Select(o => KeyFor(told, provider, window.Kind, seriesReset, o.Kind, o.Detail)).ToList();
                var firstIndex = keys.FindIndex(k => !told.ContainsKey(k));
                if (firstIndex >= 0)
                {
                    var first = options[firstIndex];
                    due.Add(new Alert(keys[firstIndex], first.Kind, Title(first.Title), first.Body, first.Level));
                }
                for (var i = 0; i < options.Count; i++)
                {
                    if (!told.ContainsKey(keys[i]))
                        told[keys[i]] = new ToldAlert(keys[i], provider, window.Kind, options[i].Kind, options[i].Detail, seriesReset, percent, now);
                }
            }
        }

        if (sessions is not null && settings.Enabled)
            PlanSessions(sessions, settings, told, due, now);

        var kept = told.Values.Where(t => Keep(t, inputs, now)).OrderBy(t => t.At).ToList();
        peaks.RemoveAll(p => now > p.ResetsAt + KeepAfterReset);
        var next = new AlertState(kept, peaks);
        return settings.IsSilenced(now, zone)
            ? new AlertPlan([], due, next)
            : new AlertPlan(due, [], next);
    }

    /// <summary>The session lights' alerts (the "provider" of their keys).</summary>
    public const string SessionProvider = "claude-code";

    /// <summary>A finished turn shorter than this is no news.</summary>
    public static readonly TimeSpan DoneMinTurn = TimeSpan.FromMinutes(1);

    /// <summary>A finish noticed later than this (the widget started after it) is old news.</summary>
    public static readonly TimeSpan DoneTooOld = TimeSpan.FromMinutes(10);

    /// <summary>
    /// <b>NeedsYou</b>: a session has waited for the person (a permission, a
    /// question, an MCP server's input) for the attention delay; once per
    /// waiting episode (its start is in the key). <b>AgentDone</b> (off by
    /// default): a turn of a minute or more finished; once per finish.
    /// </summary>
    private static void PlanSessions(IReadOnlyList<SessionActivity> sessions, AlertSettings settings,
        Dictionary<string, ToldAlert> told, List<Alert> due, DateTimeOffset now)
    {
        foreach (var session in sessions)
        {
            var episode = session.Since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            if (session.State == ActivityState.NeedsYou && settings.OnNeedsYou && now - session.Since >= settings.AttentionDelay)
            {
                var key = Key(SessionProvider, "session:" + session.SessionId, null, AlertKind.NeedsYou, episode);
                if (told.ContainsKey(key))
                    continue;
                told[key] = new ToldAlert(key, SessionProvider, "session:" + session.SessionId, AlertKind.NeedsYou, episode,
                    session.Since, 0, now);
                due.Add(new Alert(key, AlertKind.NeedsYou, "Claude needs you",
                    $"{session.Project}: {session.WaitLong} ({Format.Duration(now - session.Since)}).", UsageLevel.Orange));
            }
            else if (session.State == ActivityState.Done && settings.OnDone && session.LastTurn is { } turn &&
                     turn >= DoneMinTurn && now - session.Since <= DoneTooOld)
            {
                var key = Key(SessionProvider, "session:" + session.SessionId, null, AlertKind.AgentDone, episode);
                if (told.ContainsKey(key))
                    continue;
                told[key] = new ToldAlert(key, SessionProvider, "session:" + session.SessionId, AlertKind.AgentDone, episode,
                    session.Since, 0, now);
                due.Add(new Alert(key, AlertKind.AgentDone, "Claude finished",
                    $"{session.Project} ({Format.Duration(turn)}).", UsageLevel.Green));
            }
        }
    }

    /// <summary>The alerts that hold for this window, most urgent first.</summary>
    private static List<Option> Options(UsageWindow window, ProviderUsage usage, PaceForecast? forecast, bool trusted,
        bool hitSoon, AlertSettings settings, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture, string today)
    {
        var options = new List<Option>();
        if (!settings.Enabled)
            return options;
        var percent = window.PercentUsed;
        var name = Name(window.Kind, usage);
        var limit = LimitName(window.Kind, usage);
        var at = WholePercent(percent);
        // A per-model weekly window resets with the weekly one.
        var resets = (window.ResetsAt ?? SiblingReset(usage, window)) is { } r ? "resets " + Format.DayTime(r, now, zone, culture) : null;
        var spend = IsMonthlySpend(window.Kind) ? usage.Spend : null;

        if (settings.OnLimit && percent >= 100)
        {
            var body = resets is not null ? Sentence(resets)
                : spend is { UsedUsd: { } used, LimitUsd: { } cap } ? $"{Format.Usd(used)} of {Format.Usd(cap)} used."
                : $"{name} at {at}.";
            options.Add(new Option(AlertKind.LimitReached, "", Capital(limit) + " reached", body, UsageLevel.Red));
        }

        if (settings.OnForecast && hitSoon && forecast!.HitsLimitAt is { } hits)
        {
            var time = Format.ShortTime(hits, zone, culture);
            var body = $"At this pace you hit the {limit} at {time} (in {Format.Duration(hits - now)}). Now at {at}" +
                       (resets is not null ? $", {resets}." : ".");
            options.Add(new Option(AlertKind.ForecastHit, "", $"{Capital(limit)} at about {time}", body, UsageLevel.Orange));
        }

        if (settings.OnForecast && trusted && percent < 100 && IsWeekly(window) && forecast!.HitsLimitAt is { } runsOut &&
            window.ResetsAt is { } weekReset)
        {
            var body = $"At this pace the {limit} runs out about {Format.DayTime(runsOut, now, zone, culture)}, " +
                       $"before it resets {Format.DayTime(weekReset, now, zone, culture)}. Now at {at}.";
            options.Add(new Option(AlertKind.WeeklyRunway, today, $"{Capital(limit)} ahead", body, UsageLevel.Yellow));
        }

        if (settings.OnForecast && trusted && percent < 100 && IsSpend(window.Kind) && forecast!.ProjectedAtReset is { } projected &&
            projected > 100)
        {
            var reset = forecast.ResetsAt;
            var by = reset is { } end ? "by " + Format.LastDay(end, zone) : "by the reset";
            var body = spend is { LimitUsd: { } cap and > 0 }
                ? $"On pace for {Format.Usd(Math.Round(cap * (decimal)projected / 100m, 2))} {by} (cap {Format.Usd(cap)})."
                : $"On pace for {WholePercent(projected)} of your {limit} {by}.";
            options.Add(new Option(AlertKind.SpendPace, today, "On pace to pass your " + limit, body, UsageLevel.Orange));
        }

        if (settings.OnThreshold)
        {
            var body = resets is not null ? Sentence(resets)
                : spend is { UsedUsd: { } used, LimitUsd: { } cap } ? $"{Format.Usd(used)} of your {Format.Usd(cap)} cap."
                : $"{usage.DisplayName} doesn't say when it resets.";
            if (percent >= settings.RedPercent)
                options.Add(new Option(AlertKind.Threshold, Threshold(settings.RedPercent), $"{name} at {at}", body, UsageLevel.Red));
            if (percent >= settings.OrangePercent)
                options.Add(new Option(AlertKind.Threshold, Threshold(settings.OrangePercent), $"{name} at {at}", body, UsageLevel.Orange));
        }
        return options;
    }

    /// <summary>A told alert is forgotten a day after its window reset; one
    /// for a window without a reset after 35 days, or as soon as that window
    /// reads clearly lower than it did (it started over).</summary>
    private static bool Keep(ToldAlert told, IReadOnlyList<AlertInput> inputs, DateTimeOffset now)
    {
        if (told.ResetsAt is { } reset)
            return now <= reset + KeepAfterReset;
        if (now - told.At > KeepWithoutReset)
            return false;
        var current = inputs.Where(i => i.Usage.ProviderId == told.Provider)
            .SelectMany(i => i.Usage.Windows).FirstOrDefault(w => w.Kind == told.Kind);
        return current is null || current.PercentUsed > told.Percent - StartedOverDrop;
    }

    private static void TrackPeak(List<WindowPeak> peaks, string provider, string kind, DateTimeOffset resetsAt, double percent)
    {
        var index = peaks.FindIndex(p => p.Provider == provider && p.Kind == kind && PaceCalculator.SameReset(p.ResetsAt, resetsAt));
        if (index < 0)
            peaks.Add(new WindowPeak(provider, kind, resetsAt, percent));
        else if (percent > peaks[index].Peak)
            peaks[index] = peaks[index] with { Peak = percent };
    }

    /// <summary>A per-model weekly window has no reset of its own: it starts
    /// over with the weekly window.</summary>
    private static DateTimeOffset? SiblingReset(ProviderUsage usage, UsageWindow window) =>
        window.Kind.StartsWith(UsageWindowKinds.SevenDay + "_", StringComparison.Ordinal)
            ? usage.Window(UsageWindowKinds.SevenDay)?.ResetsAt
            : null;

    /// <summary>The key an alert was already told under, when one matches (two
    /// readings may date one reset a few seconds apart, across a minute), else a new one.</summary>
    private static string KeyFor(Dictionary<string, ToldAlert> told, string provider, string kind, DateTimeOffset? resetsAt,
        AlertKind alertKind, string detail) =>
        told.Values.FirstOrDefault(t => t.Provider == provider && t.Kind == kind && t.AlertKind == alertKind &&
                                        t.Detail == detail && PaceCalculator.SameReset(t.ResetsAt, resetsAt))?.Key
        ?? Key(provider, kind, resetsAt, alertKind, detail);

    /// <summary><c>provider|kind|reset (to the minute, UTC)|alert kind|threshold or day</c>.</summary>
    public static string Key(string provider, string kind, DateTimeOffset? resetsAt, AlertKind alertKind, string detail) =>
        string.Join('|', provider, kind,
            resetsAt is { } r ? r.ToUniversalTime().ToString("yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture) : "none",
            alertKind.ToString(), detail);

    private static string Threshold(double percent) => percent.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Never rounds up to a threshold not reached: 79.6% is "79%".</summary>
    private static string WholePercent(double percent) =>
        Math.Floor(percent + 1e-9).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static bool IsWeekly(UsageWindow window) =>
        window.Kind.StartsWith(UsageWindowKinds.SevenDay, StringComparison.Ordinal) || window.Length == TimeSpan.FromDays(7);

    private static bool IsMonthlySpend(string kind) => kind == UsageWindowKinds.MonthSpend;

    private static bool IsSpend(string kind) => kind is UsageWindowKinds.MonthSpend or UsageWindowKinds.SpendLimit;

    /// <summary>"5-hour window", "Weekly window", "Monthly spend"; for a kind not listed here the
    /// provider's own name ("Daily", "codex-other · 5-hour window").</summary>
    private static string Name(string kind, ProviderUsage? usage = null) => kind switch
    {
        UsageWindowKinds.FiveHour => "5-hour window",
        UsageWindowKinds.SevenDay => "Weekly window",
        UsageWindowKinds.SevenDayOpus => "Weekly Opus window",
        UsageWindowKinds.SevenDaySonnet => "Weekly Sonnet window",
        UsageWindowKinds.MonthSpend => "Monthly spend",
        UsageWindowKinds.SpendLimit => "Spend limit",
        _ => Labelled(kind, usage) ?? Format.WindowName(kind)
    };

    private static string? Labelled(string kind, ProviderUsage? usage) =>
        usage is not null && usage.WindowLabels.TryGetValue(kind, out var label) ? label : null;

    /// <summary>"5-hour limit", "weekly limit", "monthly cap".</summary>
    private static string LimitName(string kind, ProviderUsage usage) => kind switch
    {
        UsageWindowKinds.FiveHour => "5-hour limit",
        UsageWindowKinds.SevenDay => "weekly limit",
        UsageWindowKinds.SevenDayOpus => "weekly Opus limit",
        UsageWindowKinds.SevenDaySonnet => "weekly Sonnet limit",
        UsageWindowKinds.MonthSpend => "monthly cap",
        UsageWindowKinds.SpendLimit => "spend limit",
        _ => Lower(Labelled(kind, usage) ?? Format.WindowName(kind)) + " limit"
    };

    /// <summary>"5-hour window", "weekly window", "monthly cap", for "the … has reset".</summary>
    private static string Noun(string kind, ProviderUsage usage) => kind switch
    {
        UsageWindowKinds.MonthSpend => "monthly cap",
        UsageWindowKinds.SpendLimit => "spend limit",
        _ => Lower(Name(kind, usage))
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string Sentence(string text) => Capital(text) + ".";

    /// <summary>What Settings → Send a test alert shows.</summary>
    public static Alert TestAlert() => new("test", AlertKind.Test, "Test alert",
        "Alerts look like this: one when a limit is near, one when it is reached, one when it resets.", UsageLevel.Yellow);
}
