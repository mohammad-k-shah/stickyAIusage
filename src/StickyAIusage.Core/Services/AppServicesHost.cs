using StickyAIusage.Stats;

namespace StickyAIusage.Services;

/// <summary>
/// Holds a window's <see cref="AppServices"/> and rebuilds them when the
/// usage source changes in Settings, so the switch applies without a restart.
/// The rebuild waits until the window starts its next refresh: by then no
/// usage fetch is running (one refresh at a time, <c>RefreshGate</c>), so the
/// old graph's HTTP client and feed watcher are never disposed under a
/// request. The window subscribes to <see cref="UsageChanged"/> once; it
/// follows whichever graph is current. Not thread-safe: both apps call it on
/// their UI thread only.
/// </summary>
public sealed class AppServicesHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Func<AppSettings, AppServices> _create;
    private readonly ClaudeSessionLogSource? _sessions;
    private bool _rebuildPending;

    /// <param name="create">Builds the graph; <see cref="AppServices.CreateDefault"/>
    /// in the apps (with one transcript index kept across rebuilds), a test graph in tests.</param>
    public AppServicesHost(AppSettings settings, Func<AppSettings, AppServices>? create = null)
    {
        _settings = settings;
        if (create is null && Demo.DemoMode.Active is { } demo)
            create = demo.CreateServices;
        if (create is null)
        {
            var sessions = ClaudeSessionLogSource.CreateDefault();
            _sessions = sessions;
            create = s => AppServices.CreateDefault(s, sessions);
        }
        _create = create;
        Current = _create(settings);
        Current.UsageChanged += OnUsageChanged;
    }

    public AppServices Current { get; private set; }

    private YearStatsService? _yearStats;

    /// <summary>The year card's numbers for this window's transcripts: made on first
    /// use, over the index the window keeps across Settings rebuilds.</summary>
    public YearStatsService YearStats =>
        _yearStats ??= new YearStatsService(_sessions?.Index ?? Current.Transcripts, YearStatsService.DefaultDirectory);

    /// <summary>Raised on a thread-pool thread when new usage may be waiting.</summary>
    public event Action? UsageChanged;

    /// <summary>Call after the settings were saved. True when the usage
    /// source or Check for updates changed: the next
    /// <see cref="ApplyPendingChange"/> rebuilds.</summary>
    public bool SettingsChanged()
    {
        _rebuildPending = ClaudeSourceModes.Parse(_settings.ClaudeSource) != Current.Mode ||
                          _settings.CheckForUpdates != Current.ChecksForUpdates;
        return _rebuildPending;
    }

    /// <summary>Call at the start of a refresh, when no fetch is running.
    /// Rebuilds the graph if the source changed and disposes the old one;
    /// returns true when it did.</summary>
    public bool ApplyPendingChange()
    {
        if (!_rebuildPending)
            return false;
        _rebuildPending = false;

        var old = Current;
        AppServices next;
        try
        {
            next = _create(_settings);
        }
        catch (Exception ex)
        {
            // Keep reading the old way rather than nothing; the log says why
            // the new source didn't start. Saving Settings again retries.
            DiagnosticLog.WriteError($"AppServicesHost: switching to {ClaudeSourceModes.Parse(_settings.ClaudeSource).ToSetting()} failed; " +
                                     $"still reading as {old.Mode.ToSetting()}", ex);
            return false;
        }
        // Keep what the window set on the feed since the last build.
        next.Feed.Layout = old.Feed.Layout;
        next.Feed.ShowSessionLights = old.Feed.ShowSessionLights;
        old.UsageChanged -= OnUsageChanged;
        next.UsageChanged += OnUsageChanged;
        Current = next;
        if (old.Mode != next.Mode)
            DiagnosticLog.WriteInfo($"Usage source changed in Settings: {old.Mode.ToSetting()} → {next.Mode.ToSetting()}.");
        if (old.ChecksForUpdates != next.ChecksForUpdates)
            DiagnosticLog.WriteInfo($"Check for updates turned {(next.ChecksForUpdates ? "on" : "off")} in Settings.");
        old.Dispose();
        return true;
    }

    private void OnUsageChanged() => UsageChanged?.Invoke();

    public void Dispose()
    {
        Current.UsageChanged -= OnUsageChanged;
        Current.Dispose();
        _sessions?.Dispose();
    }
}
