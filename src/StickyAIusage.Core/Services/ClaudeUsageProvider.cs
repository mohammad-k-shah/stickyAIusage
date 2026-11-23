using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Claude's account usage, chosen from an ordered list of sources (the
/// usage endpoint today, the companion plugin's feed next).
///
/// Selection rule:
/// - Sources are asked from the most trusted down. A less trusted source is
///   <b>not asked at all</b> while a more trusted one has a fresh reading, so
///   an official feed keeps the undocumented endpoint quiet.
/// - Fresh means not stale and younger than 60 seconds while Claude Code is
///   working (a transcript changed in the last two minutes), or younger than
///   10 minutes otherwise - the same pace the endpoint source polls at.
/// - Of the readings collected, the newest non-stale one wins; readings
///   within two minutes of it count as equally new, and then the more trusted
///   one wins. With every reading stale, the same rule picks among them.
///
/// With only the endpoint source registered, it is always asked and its
/// reading is returned as is (plus its source id and trust): the behavior of
/// v1.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    internal static readonly TimeSpan ActiveFreshness = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan IdleFreshness = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan SameAgeTolerance = TimeSpan.FromMinutes(2);

    private readonly IReadOnlyList<IClaudeUsageSource> _sources;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<DateTime?> _activityProbe;
    private string? _lastChosenSourceId;

    /// <param name="sources">Any order; they are asked most trusted first,
    /// and in the given order among equals.</param>
    /// <param name="activityProbe">The newest transcript write (UTC), which
    /// decides how young a reading must be to count as fresh.</param>
    /// <param name="noReadingDetail">What the Unknown layout says when no
    /// source answered (for example, reading usage is switched off).</param>
    public ClaudeUsageProvider(IEnumerable<IClaudeUsageSource> sources, Func<DateTimeOffset> clock, Func<DateTime?> activityProbe,
        string? noReadingDetail = null)
    {
        _sources = sources.OrderBy(s => s.Trust).ToList();
        _clock = clock;
        _activityProbe = activityProbe;
        _noReadingDetail = noReadingDetail;
    }

    private readonly string? _noReadingDetail;

    public string ProviderId => "claude";
    public string DisplayName => "Claude";

    public IReadOnlyList<IClaudeUsageSource> Sources => _sources;

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock();
        // The activity probe asks the transcript index; it runs only when
        // there is a less trusted source to decide about - never with one source.
        TimeSpan? freshness = null;
        var readings = new List<(IClaudeUsageSource Source, UsageSnapshot Snapshot)>();

        foreach (var source in _sources)
        {
            var moreTrusted = readings.Where(r => r.Source.Trust < source.Trust).ToList();
            if (moreTrusted.Count > 0)
            {
                freshness ??= FreshnessLimit(now, _activityProbe());
                if (moreTrusted.Any(r => IsFresh(r.Snapshot, now, freshness.Value)))
                    continue;
            }

            UsageSnapshot? snapshot;
            try
            {
                snapshot = await source.TryGetAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A source must not take the widget down; the others still count.
                DiagnosticLog.WriteError($"ClaudeUsageProvider ({source.SourceId})", ex);
                snapshot = null;
            }

            if (snapshot is not null)
                readings.Add((source, snapshot with { SourceId = source.SourceId, Trust = source.Trust }));
        }

        if (Choose(readings) is not { } chosen)
            return new UsageSnapshot { IsStale = true, FetchedAt = now, StatusDetail = _noReadingDetail };

        if (chosen.Source.SourceId != _lastChosenSourceId)
        {
            DiagnosticLog.WriteInfo($"Usage source: {chosen.Source.SourceId} ({chosen.Source.Trust}).");
            _lastChosenSourceId = chosen.Source.SourceId;
        }
        return chosen.Snapshot;
    }

    public async Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default) =>
        (await GetUsageAsync(cancellationToken)).ToProviderUsage(ProviderId, DisplayName);

    internal static TimeSpan FreshnessLimit(DateTimeOffset now, DateTime? lastTranscriptWriteUtc) =>
        lastTranscriptWriteUtc is { } write && now - new DateTimeOffset(DateTime.SpecifyKind(write, DateTimeKind.Utc)) < ActivityWindow
            ? ActiveFreshness
            : IdleFreshness;

    internal static bool IsFresh(UsageSnapshot snapshot, DateTimeOffset now, TimeSpan limit) =>
        !snapshot.IsStale && snapshot.HasReading && now - snapshot.FetchedAt < limit;

    /// <summary>The newest reading, preferring non-stale ones, and readings
    /// with numbers over empty ones (a source that only says why it has
    /// nothing, like the feed before the plugin is installed); among readings
    /// within <see cref="SameAgeTolerance"/> of the newest, the most trusted
    /// (the list is already in trust order, so the first such one).</summary>
    private static (IClaudeUsageSource Source, UsageSnapshot Snapshot)? Choose(
        List<(IClaudeUsageSource Source, UsageSnapshot Snapshot)> readings)
    {
        if (readings.Count == 0)
            return null;
        // Numbers first; then a sign-in request (it names the fix); then the rest.
        var best = readings.Min(r => Rank(r.Snapshot));
        readings = readings.Where(r => Rank(r.Snapshot) == best).ToList();
        var pool = readings.Any(r => !r.Snapshot.IsStale) ? readings.Where(r => !r.Snapshot.IsStale).ToList() : readings;
        var newest = pool.Max(r => r.Snapshot.FetchedAt);
        return pool.First(r => newest - r.Snapshot.FetchedAt <= SameAgeTolerance);

        static int Rank(UsageSnapshot snapshot) => snapshot.HasReading ? 0 : snapshot.RequiresReauth ? 1 : 2;
    }
}
