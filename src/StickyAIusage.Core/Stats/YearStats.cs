using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Stats;

/// <summary>How far a pass is: transcripts read of those it has to read.</summary>
public readonly record struct StatsProgress(int Done, int Total);

/// <summary>What the year card shows: a year of Claude Code from the daily rollups.</summary>
public sealed record YearSummary(
    int Year,
    DateOnly From,
    DateOnly To,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal EstimatedCostUsd,
    bool CostIsRough,
    int Sessions,
    int ActiveDays,
    string? FavoriteModel,
    DateOnly? BusiestDay,
    long BusiestDayTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Adds up the days from <paramref name="from"/> to <paramref name="to"/>.
    /// Sessions count once however many days they ran; the favorite model is the one
    /// with the most estimated cost (tokens break a tie), as the share card's top
    /// model; the busiest day has the most tokens (the earlier one on a tie).</summary>
    public static YearSummary Sum(int year, DateOnly from, DateOnly to, IReadOnlyDictionary<DateOnly, DayStats> days)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        decimal cost = 0;
        var rough = false;
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, (decimal Cost, long Tokens)>(StringComparer.Ordinal);
        var activeDays = 0;
        DateOnly? busiest = null;
        long busiestTokens = 0;
        foreach (var (date, day) in days.Where(d => d.Key >= from && d.Key <= to).OrderBy(d => d.Key))
        {
            var tokens = day.Tokens;
            if (tokens == 0)
                continue;
            activeDays++;
            if (tokens > busiestTokens)
            {
                busiest = date;
                busiestTokens = tokens;
            }
            sessions.UnionWith(day.Sessions);
            rough |= day.CostIsRough;
            foreach (var (id, use) in day.Models)
            {
                input += use.Input;
                output += use.Output;
                cacheRead += use.CacheRead;
                cacheWrite += use.CacheWrite;
                cost += use.CostUsd;
                if (id == DayStats.UnknownModel)
                    continue;
                var name = Format.ModelName(id);
                var (c, t) = models.GetValueOrDefault(name);
                models[name] = (c + use.CostUsd, t + use.Tokens);
            }
        }

        var favorite = models
            .OrderByDescending(m => m.Value.Cost)
            .ThenByDescending(m => m.Value.Tokens)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => (string?)m.Key)
            .FirstOrDefault();
        return new YearSummary(year, from, to, input, output, cacheRead, cacheWrite, Math.Round(cost, 2), rough,
            sessions.Count, activeDays, favorite, busiest, busiestTokens);
    }
}

/// <summary>What one pass did (the log line, the self-test and the tests read it).</summary>
public sealed record YearPass(YearSummary Summary, int FilesRead, int DaysFromCache, int DaysSaved, TimeSpan Duration);

/// <summary>
/// The year card's numbers: one pass over the year's session transcripts through
/// the <see cref="TranscriptIndex"/> (never a walk of its own), rolled up per local
/// day and kept in <see cref="DailyStatsStore"/>. A day is stored once it has been
/// over for an hour, so a later pass reads only the transcripts written since the
/// first day it doesn't have yet — on most days, today's. The pass parses in its
/// own loop (not the session reader's cache, which serves the flyout) with the
/// same counting rules: <see cref="ClaudeSessionLogReader.CountResponse"/>, the
/// session's own records only (a fork's copied parent records are left out),
/// subagent turns and subagent files left out.
/// </summary>
public sealed class YearStatsService
{
    /// <summary>How long after a day ends it is taken as finished: a response written
    /// just after midnight may still carry the day before's time.</summary>
    internal static readonly TimeSpan FinishedAfter = TimeSpan.FromHours(1);

    /// <summary>A pass this recent answers again without reading anything.</summary>
    internal static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(1);

    private readonly TranscriptIndex _index;
    private readonly DailyStatsStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo? _zone;
    private readonly object _lock = new();
    private readonly List<IProgress<StatsProgress>> _listeners = [];
    private Task<YearSummary>? _running;
    private int _runningYear;
    private (YearPass Pass, DateTimeOffset At)? _last;

    public YearStatsService(TranscriptIndex index, string statsDirectory, Func<DateTimeOffset>? clock = null, TimeZoneInfo? zone = null)
    {
        _index = index;
        _store = new DailyStatsStore(statsDirectory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _zone = zone;
    }

    /// <summary>The profile's <c>cache/stats</c> folder.</summary>
    public static string DefaultDirectory => Path.Combine(AppPaths.CacheDirectory, "stats");

    /// <summary>The last pass that finished (null before the first).</summary>
    public YearPass? LastPass
    {
        get
        {
            lock (_lock)
                return _last?.Pass;
        }
    }

    /// <summary>The year's summary, read in the background. A pass already under way
    /// for that year is shared (its progress reported to every caller); one that
    /// finished a minute ago or less is reused. Never throws: an unreadable transcript
    /// is skipped, and a pass that fails as a whole is logged and gives an empty year.</summary>
    public Task<YearSummary> GetAsync(int year, IProgress<StatsProgress>? progress = null)
    {
        lock (_lock)
        {
            var now = _clock();
            if (_last is { } last && last.Pass.Summary.Year == year && now >= last.At && now - last.At <= ReuseFor)
                return Task.FromResult(last.Pass.Summary);
            if (_running is { } running)
            {
                // One pass at a time: another year's waits for it.
                if (_runningYear != year)
                    return running.ContinueWith(_ => GetAsync(year, progress), TaskScheduler.Default).Unwrap();
                if (progress is not null)
                    _listeners.Add(progress);
                return running;
            }
            if (progress is not null)
                _listeners.Add(progress);
            _runningYear = year;
            return _running = Task.Run(() => Run(year));
        }
    }

    private YearSummary Run(int year)
    {
        try
        {
            var pass = Scan(year, Report);
            DiagnosticLog.WriteInfo($"Year stats: {year} read in {pass.Duration.TotalSeconds:0.0} s ({pass.FilesRead} transcripts read, " +
                                    $"{pass.DaysFromCache} days from the cache, {pass.DaysSaved} saved).");
            lock (_lock)
                _last = (pass, _clock());
            return pass.Summary;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("YearStatsService.Run", ex);
            var first = new DateOnly(year, 1, 1);
            return YearSummary.Sum(year, first, first, new Dictionary<DateOnly, DayStats>());
        }
        finally
        {
            lock (_lock)
            {
                _running = null;
                _listeners.Clear();
            }
        }
    }

    private void Report(StatsProgress progress)
    {
        IProgress<StatsProgress>[] listeners;
        lock (_lock)
            listeners = [.. _listeners];
        foreach (var listener in listeners)
            listener.Report(progress);
    }

    /// <summary>One pass, on the caller's thread.</summary>
    internal YearPass Scan(int year, Action<StatsProgress>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var now = _clock();
        var zone = _zone ?? TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = new DateOnly(year, 1, 1);
        var last = year < today.Year ? new DateOnly(year, 12, 31) : today;
        var days = new Dictionary<DateOnly, DayStats>();
        var catalog = ModelCatalog.Current;
        var prices = PricesId(catalog);
        if (last < first)
            return new YearPass(YearSummary.Sum(year, first, first, days), 0, 0, 0, timer.Elapsed);

        var finished = new HashSet<DateOnly>();
        var readOnlyMonths = new HashSet<int>();
        for (var month = 1; month <= last.Month; month++)
        {
            var cached = _store.Load(year, month, zone.Id, out var writable, prices);
            foreach (var (date, day) in cached)
            {
                if (date > last)
                    continue;
                days[date] = day;
                finished.Add(date);
            }
            if (!writable)
                readOnlyMonths.Add(month);
        }
        var fromCache = finished.Count;

        var missing = new HashSet<DateOnly>();
        for (var date = first; date <= last; date = date.AddDays(1))
            if (!days.ContainsKey(date))
                missing.Add(date);

        var filesRead = 0;
        var saved = 0;
        if (missing.Count > 0)
        {
            // A transcript last written before the first missing day began holds
            // nothing for it: its records are all older than its last write.
            var since = LocalMidnightUtc(missing.Min(), zone);
            var files = _index.WrittenBetween(since, DateTime.MaxValue)
                .Where(f => ClaudeSessionLogReader.IsSessionTranscript(f.Path))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var read = new Dictionary<DateOnly, DayStats>();
            progress?.Invoke(new StatsProgress(0, files.Count));
            foreach (var file in files)
            {
                ReadTranscript(file.Path, zone, missing, read, catalog);
                filesRead++;
                progress?.Invoke(new StatsProgress(filesRead, files.Count));
            }

            var changedMonths = new HashSet<int>();
            foreach (var date in missing)
            {
                days[date] = read.GetValueOrDefault(date) ?? new DayStats();
                if (LocalMidnightUtc(date.AddDays(1), zone) + FinishedAfter <= now.UtcDateTime)
                {
                    finished.Add(date);
                    changedMonths.Add(date.Month);
                }
            }
            foreach (var month in changedMonths.Where(m => !readOnlyMonths.Contains(m)))
            {
                var keep = days.Where(d => d.Key.Month == month && finished.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value);
                if (_store.Save(year, month, zone.Id, prices, keep))
                    saved += keep.Keys.Count(missing.Contains);
            }
        }

        return new YearPass(YearSummary.Sum(year, first, last, days), filesRead, fromCache, saved, timer.Elapsed);
    }

    /// <summary>Adds one session transcript's responses on the wanted days to <paramref name="into"/>.
    /// Never throws: an unreadable file is skipped (and logged).</summary>
    internal static void ReadTranscript(string path, TimeZoneInfo zone, IReadOnlySet<DateOnly> wanted,
        Dictionary<DateOnly, DayStats> into, ModelCatalog catalog)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        // Two views, as the session reader keeps: the records of this session, and
        // every record (used only when no record names this session at all).
        var own = new TranscriptDays(zone, wanted, catalog);
        var all = new TranscriptDays(zone, wanted, catalog);
        var matched = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                // Once the session's own id was seen, a line with neither usage nor a
                // model can't change anything: skip parsing it (most of a transcript).
                if (matched && !line.Contains("\"usage\"", StringComparison.Ordinal) && !line.Contains("\"model\"", StringComparison.Ordinal))
                    continue;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // a malformed or half-written line, as the reader skips it
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;
                    var lineSessionId = ClaudeSessionLogReader.GetString(root, "sessionId");
                    if (!matched)
                        all.Apply(root);
                    if (lineSessionId is not null && !string.Equals(lineSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (lineSessionId is not null)
                        matched = true;
                    own.Apply(root);
                }
            }
        }
        catch (Exception ex)
        {
            // One odd file must not cost the whole year: it is left out, and the log says so.
            DiagnosticLog.WriteInfo($"Year stats: skipped a transcript that can't be read ({ex.GetType().Name}).");
            return;
        }

        foreach (var (date, day) in (matched ? own : all).Days)
        {
            if (!into.TryGetValue(date, out var total))
                into[date] = total = new DayStats();
            total.Add(day);
            total.Sessions.Add(sessionId);
        }
    }

    /// <summary>The responses of one view of a transcript, by local day.</summary>
    private sealed class TranscriptDays(TimeZoneInfo zone, IReadOnlySet<DateOnly> wanted, ModelCatalog catalog)
    {
        private readonly HashSet<string> _seenMessageIds = [];
        private string? _model;

        public Dictionary<DateOnly, DayStats> Days { get; } = [];

        public void Apply(JsonElement root)
        {
            if (ClaudeSessionLogReader.GetBool(root, "isSidechain"))
                return;
            if (ClaudeSessionLogReader.GetString(root, "type") != "assistant" ||
                !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return;
            if (ClaudeSessionLogReader.GetString(message, "model") is { } model && model != "<synthetic>")
                _model = model;
            // Counted (so its id is taken) even when the day isn't wanted: a repeat of
            // the same response on another line must not count again.
            if (ClaudeSessionLogReader.CountResponse(message, _seenMessageIds, _model, catalog) is not { TotalTokens: > 0 } response)
                return;
            if (ClaudeSessionLogReader.GetTimestamp(root) is not { } at)
                return; // can't be placed on a day
            var local = TimeZoneInfo.ConvertTime(at, zone);
            var date = DateOnly.FromDateTime(local.DateTime);
            if (!wanted.Contains(date))
                return;
            if (!Days.TryGetValue(date, out var day))
                Days[date] = day = new DayStats();
            day.Add(response, local.Hour);
        }
    }

    /// <summary>Which price list the costs come from: its date and where it came from.</summary>
    internal static string PricesId(ModelCatalog catalog) =>
        catalog.Updated.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " " + catalog.Origin;

    /// <summary>When a local day starts, in UTC (a midnight that a clock change skips: the hour after).</summary>
    internal static DateTime LocalMidnightUtc(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight))
            midnight = midnight.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
    }
}
