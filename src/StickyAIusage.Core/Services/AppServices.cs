using System.IO;
using System.Net.Http;
using StickyAIusage.Alerts;
using StickyAIusage.Forecasting;
using StickyAIusage.Updates;

namespace StickyAIusage.Services;

/// <summary>
/// Builds the app's object graph in one place: the usage sources, the
/// provider that chooses between them, the session reader and the feed the
/// windows poll. Both apps call <see cref="CreateDefault"/>; tests build the
/// pieces themselves with fakes. Disposing it releases the HTTP client and the
/// feed folder's watcher.
/// </summary>
public sealed class AppServices : IDisposable
{
    /// <summary>The widget's pace forecast in the feed folder, for the Claude Code plugin.</summary>
    public const string ForecastFileName = "forecast.json";

    public const string UsageOffDetail =
        "Reading plan limits is turned off (Settings → Usage source → Off).";

    /// <summary>At most one refresh a second while Claude Code writes a session transcript.</summary>
    internal static readonly TimeSpan TranscriptRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly List<IDisposable> _owned;
    private readonly TranscriptIndex _transcripts;
    private readonly ChangeThrottle _transcriptChanges;

    private AppServices(UsageFeed feed, ClaudeUsageProvider claude, ClaudeFeedUsageSource claudeFeed, TranscriptIndex transcripts,
        List<IDisposable> owned)
    {
        Feed = feed;
        Claude = claude;
        ClaudeFeed = claudeFeed;
        _owned = owned;
        _transcripts = transcripts;
        _transcriptChanges = new ChangeThrottle(TranscriptRefreshInterval, OnFeedChanged);
        claudeFeed.Changed += OnFeedChanged;
        // A new session (or a new answer in one) shows without waiting for the next tick.
        transcripts.TranscriptWritten += OnTranscriptWritten;
    }

    private void OnTranscriptWritten(string path)
    {
        if (ClaudeSessionLogReader.IsSessionTranscript(path))
            _transcriptChanges.Signal();
    }

    public UsageFeed Feed { get; }

    public ClaudeUsageProvider Claude { get; }

    /// <summary>The transcript index this graph reads sessions through.</summary>
    public TranscriptIndex Transcripts => _transcripts;

    /// <summary>The source setting this graph was built for.</summary>
    public ClaudeSourceMode Mode { get; private init; }

    /// <summary>The Check for updates setting this graph was built for: when
    /// on, it checks once a day for a newer version and a newer price list.</summary>
    public bool ChecksForUpdates { get; private init; }

    /// <summary>The daily "is there a newer version?" check; null while Check for updates is off.</summary>
    public UpdateChecker? AppUpdates { get; private init; }

    /// <summary>The Claude Code plugin's feed folder: a usage source in Auto and
    /// Feed, read for the session lights and exact costs in every mode.</summary>
    public ClaudeFeedUsageSource ClaudeFeed { get; }

    /// <summary>Raised on a thread-pool thread when new usage may be waiting
    /// (the plugin wrote a record, or Claude Code a session transcript - at most
    /// once a second): the windows refresh at once instead of on
    /// their next tick.</summary>
    public event Action? UsageChanged;

    /// <summary>The default graph, reading the paths from <see cref="AppPaths"/>.
    /// The sources follow <see cref="AppSettings.ClaudeSource"/>: the plugin's
    /// feed ahead of the usage endpoint ("auto"), either one alone, or none.</summary>
    /// <param name="sessions">The transcripts to read, kept by the caller across
    /// rebuilds (<see cref="AppServicesHost"/>: one index per window, not one walk
    /// per Settings change); null: a new one, disposed with this graph.</param>
    public static AppServices CreateDefault(AppSettings settings, ClaudeSessionLogSource? sessions = null)
    {
        var ownsSessions = sessions is null;
        sessions ??= ClaudeSessionLogSource.CreateDefault();
        // Codex's limits from its own session logs: shown when Codex is on this machine.
        IReadOnlyList<IProviderUsageReader> others =
            [new CodexUsageProvider(AppPaths.CodexDirectory, () => DateTimeOffset.UtcNow)];
        return Create(settings, sessions, AppPaths.FeedDirectory, watchFeed: true, CreateOAuthSource, CreatePriceUpdater,
            ownsSessions: ownsSessions, others: others, createUpdateChecker: CreateUpdateChecker);

        (IClaudeUsageSource, IReadOnlyList<IDisposable>) CreateOAuthSource(Func<DateTimeOffset> clock)
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var oauth = new ClaudeOAuthUsageSource(httpClient, ClaudeCredentialsReader.ForCurrentPlatform,
                ClaudeCredentialsReader.CredentialsPath, AppPaths.CacheDirectory, clock, sessions.GetMostRecentActivityUtc);
            return (oauth, [oauth, httpClient]);
        }

        (ModelCatalogUpdater, IReadOnlyList<IDisposable>) CreatePriceUpdater(Func<DateTimeOffset> clock)
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var updater = new ModelCatalogUpdater(httpClient, AppPaths.CacheDirectory, clock);
            return (updater, [updater, httpClient]);
        }

        (UpdateChecker, IReadOnlyList<IDisposable>) CreateUpdateChecker(Func<DateTimeOffset> clock)
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var checker = new UpdateChecker(httpClient, AppPaths.CacheDirectory, clock, AppIdentity.ReleaseVersion(),
                InstallMethods.Detect(AppContext.BaseDirectory, OperatingSystem.IsMacOS()), OperatingSystem.IsMacOS());
            return (checker, [checker, httpClient]);
        }
    }

    /// <param name="createOAuthSource">Called only when the settings use the
    /// usage endpoint, so "feed" and "off" never build an HTTP client or touch
    /// the sign-in. Returns the source and what to dispose with it.</param>
    /// <param name="createPriceUpdater">Called only when Check for updates is
    /// on, so with it off nothing is ever asked of GitHub. Null: never.</param>
    /// <param name="cacheDirectory">Where the usage journal lives; null: the
    /// profile's cache folder.</param>
    /// <param name="ownsSessions">Dispose <paramref name="sessions"/> (its index's watcher) with the graph.</param>
    /// <param name="others">Other tools shown beside Claude (Codex); null: none.</param>
    /// <param name="createUpdateChecker">Called only when Check for updates is on, like
    /// <paramref name="createPriceUpdater"/>. Null: never.</param>
    internal static AppServices Create(AppSettings settings, ClaudeSessionLogSource sessions, string feedDirectory,
        bool watchFeed, Func<Func<DateTimeOffset>, (IClaudeUsageSource Source, IReadOnlyList<IDisposable> Owned)> createOAuthSource,
        Func<Func<DateTimeOffset>, (ModelCatalogUpdater Updater, IReadOnlyList<IDisposable> Owned)>? createPriceUpdater = null,
        string? cacheDirectory = null, bool ownsSessions = true, IReadOnlyList<IProviderUsageReader>? others = null,
        Func<Func<DateTimeOffset>, (UpdateChecker Checker, IReadOnlyList<IDisposable> Owned)>? createUpdateChecker = null)
    {
        Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;
        var mode = ClaudeSourceModes.Parse(settings.ClaudeSource);
        var sources = new List<IClaudeUsageSource>();
        var owned = new List<IDisposable>();
        if (ownsSessions)
            owned.Add(sessions);

        // The plugin's records also carry what each session is doing (the
        // session lights) and its exact cost, whatever the plan limits come
        // from: the folder is always read; only Auto and Feed take limits from it.
        var claudeFeed = new ClaudeFeedUsageSource(feedDirectory, clock, watch: watchFeed);
        owned.Add(claudeFeed);
        var feedGivesLimits = mode is ClaudeSourceMode.Auto or ClaudeSourceMode.Feed;
        if (feedGivesLimits)
            sources.Add(claudeFeed);
        if (mode.UsesUsageEndpoint())
        {
            var (oauth, oauthOwned) = createOAuthSource(clock);
            sources.Add(oauth);
            owned.AddRange(oauthOwned);
        }
        DiagnosticLog.WriteInfo($"Usage sources ({mode.ToString().ToLowerInvariant()}): " +
                                (sources.Count == 0 ? "none" : string.Join(", ", sources.Select(s => s.SourceId))) + ".");

        ModelCatalogUpdater? priceUpdates = null;
        if (settings.CheckForUpdates && createPriceUpdater is not null)
        {
            var (updater, updaterOwned) = createPriceUpdater(clock);
            priceUpdates = updater;
            owned.AddRange(updaterOwned);
            DiagnosticLog.WriteInfo("Prices: the published price list is checked once a day (Check for updates is on).");
        }
        UpdateChecker? appUpdates = null;
        if (settings.CheckForUpdates && createUpdateChecker is not null)
        {
            var (checker, checkerOwned) = createUpdateChecker(clock);
            appUpdates = checker;
            owned.AddRange(checkerOwned);
            DiagnosticLog.WriteInfo(checker.Running is { } running
                ? $"Updates: GitHub is asked once a day whether a version newer than {running} is out (Check for updates is on)."
                : "Updates: this build's version isn't a release version, so it doesn't look for newer ones.");
        }

        var claude = new ClaudeUsageProvider(sources, clock, sessions.GetMostRecentActivityUtc,
            mode == ClaudeSourceMode.Off ? UsageOffDetail : null);
        // The journal belongs to this profile's data folder; the forecast it
        // feeds goes to the shared feed folder, where the plugin reads it.
        var forecaster = new UsageForecaster(
            new UsageJournal(Path.Combine(cacheDirectory ?? AppPaths.CacheDirectory, "journal.jsonl"), clock),
            Path.Combine(feedDirectory, ForecastFileName), clock);
        // The alerts read the live settings object, so Settings changes apply
        // at the next refresh; what was already told is in the profile's cache.
        var alerts = new UsageAlerts(Path.Combine(cacheDirectory ?? AppPaths.CacheDirectory, AlertStateFile.FileName), clock);
        var feed = new UsageFeed(claude, sessions, new DailySpendTracker(),
            () => claudeFeed.Sessions, priceUpdates, forecaster,
            alerts: alerts, alertSettings: () => AlertSettings.From(settings),
            pluginSeen: () => claudeFeed.PluginSeen,
            readFeedSessions: feedGivesLimits ? null : () => claudeFeed.TryGetAsync(CancellationToken.None),
            others: others, providerEnabled: settings.IsProviderEnabled)
        {
            Layout = settings.CardLayout(),
            ShowSessionLights = settings.ShowSessionLights,
            AppUpdates = appUpdates
        };
        return new AppServices(feed, claude, claudeFeed, sessions.Index, owned)
        {
            Mode = mode,
            ChecksForUpdates = settings.CheckForUpdates,
            AppUpdates = appUpdates
        };
    }

    /// <summary>How often the windows refresh while the demo climbs (their own tick is 15 s).</summary>
    internal static readonly TimeSpan DemoTick = TimeSpan.FromSeconds(3);

    /// <summary>The demo's graph (<c>--demo</c>): the script's readings, sessions, lights, pace and
    /// Codex card; the real alerts on them. It reads nothing of Claude Code's, Codex's or git's
    /// and sends nothing: the folders it keeps (an empty transcript folder for the year card, the
    /// alerts' state, today's spend) are under <paramref name="root"/>, the demo's temporary profile.</summary>
    public static AppServices CreateDemo(AppSettings settings, Demo.DemoData data, string root, Func<DateTimeOffset>? clock = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        var cache = Path.Combine(root, "demo-cache");
        var feedDirectory = Path.Combine(root, "demo-feed");
        var projects = Path.Combine(root, "demo-projects");
        foreach (var folder in new[] { cache, feedDirectory, projects })
            Directory.CreateDirectory(folder);

        var sessions = new ClaudeSessionLogSource(projects);
        var claudeFeed = new ClaudeFeedUsageSource(feedDirectory, clock, watch: false);
        var claude = new ClaudeUsageProvider([], clock, () => null);
        var feed = new UsageFeed(new Demo.DemoUsageProvider(data, clock), new Demo.DemoSessionReader(data, clock),
            new DailySpendTracker(Path.Combine(cache, "day-spend.json")),
            () => data.Records(clock()), forecaster: new Demo.DemoPace(data, clock), git: new Demo.DemoGit(data),
            alerts: new UsageAlerts(Path.Combine(cache, AlertStateFile.FileName), clock),
            alertSettings: () => AlertSettings.From(settings), pluginSeen: () => true,
            others: [new Demo.DemoCodexProvider(data, clock)], providerEnabled: settings.IsProviderEnabled, clock: clock)
        {
            Layout = settings.CardLayout(),
            ShowSessionLights = settings.ShowSessionLights
        };
        var services = new AppServices(feed, claude, claudeFeed, sessions.Index, [sessions, claudeFeed])
        {
            Mode = ClaudeSourceModes.Parse(settings.ClaudeSource),
            ChecksForUpdates = settings.CheckForUpdates
        };
        // The climb is two minutes: the windows refresh every few seconds until a while after it.
        var until = data.Start + Demo.DemoData.Climb + TimeSpan.FromMinutes(1);
        Timer? ticker = null;
        ticker = new Timer(_ =>
        {
            if (clock() > until)
                ticker?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            else
                services.OnFeedChanged();
        }, null, DemoTick, DemoTick);
        services._owned.Add(ticker);
        DiagnosticLog.WriteInfo($"Demo mode ({data.Variant.ToString().ToLowerInvariant()}): sample data only; nothing of this computer is read.");
        return services;
    }

    private void OnFeedChanged() => UsageChanged?.Invoke();

    public void Dispose()
    {
        ClaudeFeed.Changed -= OnFeedChanged;
        _transcripts.TranscriptWritten -= OnTranscriptWritten;
        _transcriptChanges.Dispose();
        foreach (var item in _owned)
            item.Dispose();
    }
}
