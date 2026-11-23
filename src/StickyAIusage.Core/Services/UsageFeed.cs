using System.IO;
using StickyAIusage.Alerts;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Updates;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Services;

/// <summary>What one refresh hands the window: the usage reading, the most
/// recent session, today's spend on a monthly cap (null otherwise) and the
/// pace forecast of each window, and the notifications it deserves.</summary>
public sealed record UsageSummary(UsageSnapshot Usage, SessionInfo? Session, decimal? SpentToday)
{
    public IReadOnlyList<PaceForecast> Forecasts { get; init; } = [];

    /// <summary>What to notify about (none in quiet hours, while muted, or with alerts off).</summary>
    public IReadOnlyList<Alert> Alerts { get; init; } = [];

    /// <summary>What each Claude Code session is doing (the session lights), needs-you first.</summary>
    public IReadOnlyList<SessionActivity> Activities { get; init; } = [];

    /// <summary>The companion plugin has written to the feed folder: without it the
    /// flyout says how to get the lights instead of showing none.</summary>
    public bool HasPlugin { get; init; }

    /// <summary>The other tools found and turned on (Codex), each with its pace.</summary>
    public IReadOnlyList<ProviderReading> Others { get; init; } = [];

    /// <summary>When a waiting session's "needs you" alert falls due, if it
    /// hasn't yet: the window refreshes then instead of at its next tick.</summary>
    public DateTimeOffset? AttentionDueAt { get; init; }

    /// <summary>A newer version than this one (Check for updates on); null: none known.</summary>
    public UpdateNotice? Update { get; init; }
}

/// <summary>
/// The data behind the widget, shared by the Windows and macOS apps: the
/// usage reading plus the most recent session for the pill, and today's
/// session cards for the flyout. Everything runs on the thread pool; callers
/// marshal the results back to their UI thread.
/// </summary>
public sealed class UsageFeed
{
    private readonly IUsageProvider _usageProvider;
    private readonly ISessionReader _sessions;
    private readonly DailySpendTracker _dailySpend;
    private readonly Func<IReadOnlyList<FeedSession>>? _feedSessions;
    private readonly Func<bool>? _pluginSeen;
    private readonly Func<Task>? _readFeedSessions;
    private readonly ModelCatalogUpdater? _priceUpdates;
    private readonly IPaceForecaster? _forecaster;
    private readonly UsageAlerts? _alerts;
    private readonly Func<AlertSettings> _alertSettings;
    private readonly IGitInfoSource _git;
    private readonly IReadOnlyList<IProviderUsageReader> _others;
    private readonly Func<string, bool> _providerEnabled;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _logLock = new();
    private (string? path, long totalTokens, decimal cost)? _lastLoggedSession;

    /// <param name="usageProvider">The account reading behind the Claude
    /// layouts (monthly spend, plan limits).</param>
    /// <param name="sessions">Where session costs and cards come from.</param>
    /// <param name="dailySpend">Today's spend on a monthly-cap account.</param>
    /// <param name="feedSessions">The companion plugin's session records, whose
    /// own session costs replace the estimates where they cover the same
    /// session (<see cref="SessionCosts"/>); null without the plugin's feed.</param>
    /// <param name="priceUpdates">The daily price-list check, asked on every
    /// tick; null when Check for updates is off.</param>
    /// <param name="forecaster">Records each reading and works out the pace
    /// forecasts; null: no forecasts.</param>
    /// <param name="alerts">Plans the notifications; null: none.</param>
    /// <param name="alertSettings">The alert switches, read on the calling (UI)
    /// thread at the start of each refresh; null: the defaults.</param>
    /// <param name="git">The session cards' git and pull request info; null: read with git and gh.</param>
    /// <param name="pluginSeen">Whether the plugin ever wrote to the feed folder (its marker file).</param>
    /// <param name="others">Other tools whose limits are shown beside Claude's (Codex).</param>
    /// <param name="providerEnabled">Whether Settings shows a tool (read each refresh); null: all.</param>
    /// <param name="readFeedSessions">Reads the plugin's records when they are
    /// not one of the usage sources (Legacy, Off), so the lights still work;
    /// null when reading the usage already reads them.</param>
    /// <param name="clock">Now, for the lights, the cards and "today"; null: the system clock.</param>
    public UsageFeed(IUsageProvider usageProvider, ISessionReader sessions, DailySpendTracker dailySpend,
        Func<IReadOnlyList<FeedSession>>? feedSessions = null, ModelCatalogUpdater? priceUpdates = null,
        IPaceForecaster? forecaster = null, IGitInfoSource? git = null, UsageAlerts? alerts = null,
        Func<AlertSettings>? alertSettings = null, Func<bool>? pluginSeen = null,
        Func<Task>? readFeedSessions = null, IReadOnlyList<IProviderUsageReader>? others = null,
        Func<string, bool>? providerEnabled = null, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _others = others ?? [];
        _providerEnabled = providerEnabled ?? (_ => true);
        _pluginSeen = pluginSeen;
        _readFeedSessions = readFeedSessions;
        _usageProvider = usageProvider;
        _sessions = sessions;
        _dailySpend = dailySpend;
        _feedSessions = feedSessions;
        _priceUpdates = priceUpdates;
        _forecaster = forecaster;
        _git = git ?? new GitInfoCache();
        _alerts = alerts;
        _alertSettings = alertSettings ?? (() => AlertSettings.Default);
    }

    /// <summary>The card layout the flyout renders with; set from settings
    /// whenever they change. Read on the thread pool, so it's a plain
    /// reference swap rather than a mutable object.</summary>
    public CardLayout Layout { get; set; } = CardLayout.Default;

    /// <summary>The daily check for a newer version, asked on every tick like the
    /// price list's; null when Check for updates is off.</summary>
    public UpdateChecker? AppUpdates { get; init; }

    /// <summary>The usage reading, the most recent session, and - on a
    /// monthly-cap account - what was spent today (null otherwise).</summary>
    public Task<UsageSummary> GetSummaryAsync()
    {
        // Settings change on the UI thread: take this refresh's copy here.
        var alertSettings = _alertSettings();
        var others = _others.Where(p => Enabled(p.ProviderId)).ToList();
        return Task.Run(() => SummaryAsync(alertSettings, others));
    }

    private async Task<UsageSummary> SummaryAsync(AlertSettings alertSettings, IReadOnlyList<IProviderUsageReader> otherProviders)
    {
        _priceUpdates?.RefreshIfDue();
        AppUpdates?.RefreshIfDue();
        var usage = await _usageProvider.GetUsageAsync();
        var forecasts = Forecast(usage);
        var others = await ReadOthersAsync(otherProviders);
        // After the usage: this refresh has just read the feed folder (or reads it now).
        await ReadFeedSessionsAsync();
        var now = _clock();
        var feed = FeedSessions();
        var activities = SessionActivities.From(feed, now);
        var alerts = Alerts(usage, forecasts, others, alertSettings, activities);
        var session = _sessions.GetMostRecentSession();
        if (session is not null)
            ApplyExactCost(session);
        LogSessionSelectionIfChanged(session);

        decimal? spentToday = null;
        // The day target is in dollars: a percent-only spend limit has none.
        if (usage.HasMonthlySpend && !usage.MonthlySpendPercentOnly)
        {
            var today = Today();
            spentToday = _dailySpend.Record(usage.MonthlySpendUsedUsd, today, () => CodeSpentSince(today));
        }
        return new UsageSummary(usage, session, spentToday)
        {
            Forecasts = forecasts,
            Alerts = alerts,
            Activities = activities,
            Others = others,
            HasPlugin = feed.Count > 0 || PluginSeen(),
            AttentionDueAt = AttentionDueAt(activities, alertSettings, now),
            Update = AppUpdates?.Notice
        };
    }

    /// <summary>The earliest time a session that waits now will have waited long enough to notify.</summary>
    internal static DateTimeOffset? AttentionDueAt(IReadOnlyList<SessionActivity> activities, AlertSettings settings,
        DateTimeOffset now)
    {
        if (!settings.Enabled || !settings.OnNeedsYou)
            return null;
        var due = activities.Where(a => a.State == ActivityState.NeedsYou && a.Since + settings.AttentionDelay > now)
            .Select(a => a.Since + settings.AttentionDelay).ToList();
        return due.Count == 0 ? null : due.Min();
    }

    private async Task ReadFeedSessionsAsync()
    {
        if (_readFeedSessions is null)
            return;
        try
        {
            await _readFeedSessions();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageFeed.ReadFeedSessions", ex);
        }
    }

    private IReadOnlyList<FeedSession> FeedSessions()
    {
        try
        {
            return _feedSessions?.Invoke() ?? [];
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageFeed.FeedSessions", ex);
            return [];
        }
    }

    private bool PluginSeen()
    {
        try
        {
            return _pluginSeen?.Invoke() ?? false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageFeed.PluginSeen", ex);
            return false;
        }
    }

    /// <summary>The notifications this reading deserves. The reading goes in
    /// even when it has no numbers, so a window that reset while Claude Code
    /// was quiet can still say "you're back".</summary>
    private IReadOnlyList<Alert> Alerts(UsageSnapshot usage, IReadOnlyList<PaceForecast> forecasts,
        IReadOnlyList<ProviderReading> others, AlertSettings settings, IReadOnlyList<SessionActivity> activities)
    {
        if (_alerts is null)
            return [];
        try
        {
            // With another tool beside Claude the alerts name the tool ("Codex: 5-hour window at 86%").
            var inputs = new List<AlertInput> { new(usage.ToProviderUsage(), forecasts) };
            inputs.AddRange(others.Where(o => o.Usage.Windows.Count > 0).Select(o => new AlertInput(o.Usage, o.Forecasts)));
            return _alerts.Update(inputs, settings, activities);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageFeed.Alerts", ex);
            return [];
        }
    }

    private bool Enabled(string providerId)
    {
        try
        {
            return _providerEnabled(providerId);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageFeed.ProviderEnabled", ex);
            return false;
        }
    }

    /// <summary>The other tools that are on this machine, each read once with its
    /// pace; a tool that isn't installed (or fails) leaves nothing behind.</summary>
    private async Task<IReadOnlyList<ProviderReading>> ReadOthersAsync(IReadOnlyList<IProviderUsageReader> providers)
    {
        var readings = new List<ProviderReading>();
        foreach (var provider in providers)
        {
            try
            {
                if (!provider.IsPresent)
                    continue;
                var usage = await provider.GetProviderUsageAsync();
                IReadOnlyList<PaceForecast> paces = [];
                if (_forecaster is not null && usage.Health is (UsageHealth.Ok or UsageHealth.Stale) && usage.Windows.Count > 0)
                    paces = _forecaster.Update(usage);
                readings.Add(new ProviderReading(usage, paces));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteError($"UsageFeed.ReadOthers({provider.ProviderId})", ex);
            }
        }
        return readings;
    }

    /// <summary>The pace of each window, from this reading and the ones
    /// before it; none without a forecaster or without a reading.</summary>
    private IReadOnlyList<PaceForecast> Forecast(UsageSnapshot usage)
    {
        if (_forecaster is null || !usage.HasReading)
            return [];
        try
        {
            return _forecaster.Update(usage.ToProviderUsage());
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageFeed.Forecast", ex);
            return [];
        }
    }

    /// <summary>Estimated cost of the Claude Code sessions started today. A
    /// session carried over from yesterday is left out: its cost is for the
    /// whole session, not just today's part.</summary>
    private decimal CodeSpentSince(DateOnly today) =>
        _sessions.GetSessionsForDate(today)
            .Where(s => DateOnly.FromDateTime(s.StartedAt.LocalDateTime) == today)
            .Sum(s => s.EstimatedCostUsd);

    private DateOnly Today() => DateOnly.FromDateTime(_clock().LocalDateTime);

    /// <summary>Whether the cards carry the sessions' status chips; set from settings.</summary>
    public bool ShowSessionLights { get; set; } = true;

    /// <summary>Sessions active in the last <paramref name="days"/> days (1 =
    /// today only), with git info: the ones that need you first, then the most
    /// recently active.</summary>
    public Task<List<SessionCardViewModel>> GetCardsAsync(int days) => Task.Run(() =>
    {
        var now = _clock().ToLocalTime();
        var layout = Layout;
        var today = Today();
        var activities = ShowSessionLights
            ? SessionActivities.From(FeedSessions(), now)
                .GroupBy(a => a.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SessionActivity>();
        var cards = _sessions.GetSessionsInRange(today.AddDays(1 - days), today)
            .Select(s =>
            {
                s.Git = _git.Get(s.WorkingDirectory);
                ApplyExactCost(s);
                return SessionCardViewModel.From(s, now, layout, activities.GetValueOrDefault(s.SessionId));
            })
            .ToList();
        return SessionCardViewModel.NeedsYouFirst(cards);
    });

    /// <summary>Today's sessions as the flyout's Today tab lists them (exact costs
    /// applied, no git): what the share card counts.</summary>
    public Task<List<SessionInfo>> GetTodaySessionsAsync() => Task.Run(() =>
    {
        var today = Today();
        var sessions = _sessions.GetSessionsInRange(today, today);
        foreach (var session in sessions)
            ApplyExactCost(session);
        return sessions;
    });

    /// <summary>Claude Code's own figure for the session when the plugin's
    /// record covers it; the estimate stays otherwise.</summary>
    private void ApplyExactCost(SessionInfo session)
    {
        if (_feedSessions is null)
            return;
        try
        {
            SessionCosts.ApplyExact(session, _feedSessions());
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageFeed.ApplyExactCost", ex);
        }
    }

    /// <summary>Makes the next refresh log the selected session even if it's
    /// unchanged - used on resume, so "nothing updated for hours" and "the
    /// widget was paused" don't look identical in diagnostics.log.</summary>
    public void ForgetLoggedSession()
    {
        lock (_logLock) _lastLoggedSession = null;
    }

    /// <summary>Logs which transcript is read as "most recent" and its totals,
    /// only when something changed, so a "cost never updates" report can be
    /// diagnosed without spamming the log every 15s.</summary>
    private void LogSessionSelectionIfChanged(SessionInfo? session)
    {
        var current = session is null
            ? ((string? path, long totalTokens, decimal cost)?)null
            : (session.TranscriptPath, session.TotalTokens, session.EstimatedCostUsd);

        lock (_logLock)
        {
            if (current == _lastLoggedSession)
                return;
            _lastLoggedSession = current;
        }

        if (session is null)
        {
            DiagnosticLog.WriteInfo($"Session selection: no session found under {_sessions.Location}.");
            return;
        }

        DiagnosticLog.WriteInfo(
            $"Session selection changed: path={session.TranscriptPath} " +
            $"lastWrite={File.GetLastWriteTime(session.TranscriptPath):yyyy-MM-dd HH:mm:ss} " +
            $"model={session.Model} in={session.InputTokens} out={session.OutputTokens} " +
            $"cacheRead={session.CacheReadTokens} cacheCreate={session.CacheCreationTokens} " +
            $"total={session.TotalTokens} cost={session.EstimatedCostUsd:0.0000}");
    }
}
