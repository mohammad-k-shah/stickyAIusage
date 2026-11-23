using System.Globalization;
using System.IO;
using System.Text.Json;
using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Claude's plan limits as Claude Code itself reports them, through the
/// companion plugin (<c>plugin/</c> in this repository). The plugin writes one
/// small JSON record per session into the feed folder
/// (<see cref="AppPaths.FeedDirectory"/>) after every turn; this source reads
/// those files. No network, no sign-in: the official route.
///
/// - The account's limits come from the record whose limits Claude Code
///   reported most recently (<c>rateLimitsAt</c>, falling back to
///   <c>observedAt</c>): every session sees the same account.
/// - The plugin's writes are not atomic, so a file that fails to parse is
///   read again up to <see cref="ParseRetries"/> times, 100 ms apart; if it
///   still fails, the last good record of that file is kept until the file
///   changes again (so one left cut short is not retried on every tick).
/// - A record of an unknown schema is ignored, with one log line.
/// - Records older than seven days, or ended for more than a day, are deleted
///   (at most once an hour).
/// - With <c>watch</c>, a file-system watcher raises <see cref="Changed"/>
///   when a record is written, so the widget refreshes at once instead of on
///   its next 15-second tick.
/// </summary>
public sealed class ClaudeFeedUsageSource : IClaudeUsageSource, IDisposable
{
    public const string Id = "claude-feed";

    /// <summary>The record format this build reads.</summary>
    public const int SupportedSchema = 1;

    public const string FilePattern = "claude-*.json";

    /// <summary>A reading younger than this is shown as current; an older one
    /// as "as of 12m ago - updates while Claude Code is active".</summary>
    public static readonly TimeSpan LiveFor = TimeSpan.FromMinutes(3);

    public const string WaitingDetail =
        "Waiting for Claude Code. Install the Sticky AI Usage plugin in Claude Code, then send it one message.";

    public const string NoLimitsDetail =
        "Claude Code reports no plan limits yet. They appear after its next response (Pro and Max plans).";

    internal const int ParseRetries = 3;
    internal static readonly TimeSpan ParseRetryDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    internal static readonly TimeSpan KeepEndedFor = TimeSpan.FromDays(1);
    internal static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>A record is a few hundred bytes; anything this large is not one.</summary>
    internal const long MaxRecordBytes = 1_000_000;

    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedProblems = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private readonly bool _watch;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private volatile bool _watcherFailed;
    private bool _disposed;

    /// <param name="directory">The feed folder; it may not exist yet.</param>
    /// <param name="clock">The widget's clock: record ages are measured with it.</param>
    /// <param name="delay">Waits between parse retries (tests pass a no-op).</param>
    /// <param name="watch">Watch the folder and raise <see cref="Changed"/>
    /// (creates the folder, since a watcher needs one).</param>
    public ClaudeFeedUsageSource(string directory, Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null, bool watch = false)
    {
        _directory = directory;
        _clock = clock;
        _delay = delay ?? Task.Delay;
        _watch = watch;
        if (watch)
            StartWatching();
    }

    public string SourceId => Id;

    public SourceTrust Trust => SourceTrust.Official;

    public string FolderPath => _directory;

    /// <summary>The plugin has written its marker (<c>plugin.json</c>) here at
    /// some point: it is installed, even when no session record is left.</summary>
    public bool PluginSeen
    {
        get
        {
            try { return File.Exists(Path.Combine(_directory, MarkerFileName)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public const string MarkerFileName = "plugin.json";

    /// <summary>Raised (on a thread-pool thread, at most every 250 ms) after a
    /// record in the feed folder was created, changed or deleted.</summary>
    public event Action? Changed;

    /// <summary>Where the source's log lines go; tests count them here.</summary>
    internal Action<string> Log { get; init; } = DiagnosticLog.WriteInfo;

    /// <summary>Every session record read on the last call, newest first.</summary>
    public IReadOnlyList<FeedSession> Sessions { get; private set; } = [];

    public async Task<UsageSnapshot?> TryGetAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = _clock();
            RestartWatcherIfItFailed();
            await RefreshFilesAsync(cancellationToken);
            var sessions = _files.Values.Select(e => e.Session).OfType<FeedSession>()
                .OrderByDescending(s => s.ObservedAt).ToList();
            Sessions = sessions;
            CleanUpIfDue(now);
            return BuildSnapshot(sessions, now);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Reads the files that are new or changed since the last call.</summary>
    private async Task RefreshFilesAsync(CancellationToken cancellationToken)
    {
        List<FileInfo> files;
        try
        {
            var folder = new DirectoryInfo(_directory);
            files = folder.Exists ? folder.EnumerateFiles(FilePattern).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Keep what was read before; the next tick tries again.
            LogOnce("folder", $"Feed: could not list {_directory}: {ex.Message}");
            return;
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            present.Add(file.FullName);
            if (_files.TryGetValue(file.FullName, out var known) &&
                known.LastWriteUtc == file.LastWriteTimeUtc && known.Length == file.Length)
                continue;

            var result = await ReadRecordAsync(file, cancellationToken);
            switch (result.Kind)
            {
                case ReadKind.Record:
                    _files[file.FullName] = new Entry(file.LastWriteTimeUtc, file.Length, result.Session);
                    _loggedProblems.Remove(file.FullName);
                    break;
                case ReadKind.Refused:
                    _files[file.FullName] = new Entry(file.LastWriteTimeUtc, file.Length, null);
                    LogOnce(file.FullName, $"Feed: ignored {file.Name}: {result.Problem}.");
                    break;
                default:
                    // Still broken after the retries: keep the file's last good
                    // record under the stamp just read, so a file left cut short
                    // for good (Claude Code killed mid-write) isn't read with
                    // retries on every tick; its next write changes the stamp.
                    _files[file.FullName] = new Entry(file.LastWriteTimeUtc, file.Length, known?.Session);
                    LogOnce(file.FullName, $"Feed: could not read {file.Name} ({result.Problem}); kept its last good record.");
                    break;
            }
        }

        foreach (var gone in _files.Keys.Where(path => !present.Contains(path)).ToList())
        {
            _files.Remove(gone);
            _loggedProblems.Remove(gone);
        }
    }

    private async Task<ReadResult> ReadRecordAsync(FileInfo file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxRecordBytes)
            return ReadResult.Refuse($"{file.Length} bytes is too large for a record");

        var problem = "";
        for (var attempt = 0; attempt <= ParseRetries; attempt++)
        {
            if (attempt > 0)
                await _delay(ParseRetryDelay, cancellationToken);
            try
            {
                string text;
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = await reader.ReadToEndAsync(cancellationToken);

                var result = Parse(text, file);
                if (result.Kind != ReadKind.Broken)
                    return result;
                problem = result.Problem;
            }
            catch (FileNotFoundException)
            {
                return ReadResult.Broken("deleted while reading");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = ex.Message;
            }
        }
        return ReadResult.Broken(problem);
    }

    /// <summary>One record file's text → a session, a refusal (valid JSON this
    /// build can't use) or "broken" (worth reading again).</summary>
    internal static ReadResult Parse(string text, FileInfo file)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return ReadResult.Broken("incomplete JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ReadResult.Refuse("not a JSON object");
            if (!root.TryGetProperty("schema", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetDouble(out var schema))
                return ReadResult.Refuse("no schema number");
            if ((int)Math.Floor(schema) != SupportedSchema)
                return ReadResult.Refuse($"unknown schema {schema.ToString(CultureInfo.InvariantCulture)} (this version reads {SupportedSchema})");

            var sessionId = String(root, "sessionId") ?? SessionIdFromFileName(file.Name);
            var observedAt = Time(root, "observedAt") ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

            IReadOnlyList<UsageWindow>? limits = null;
            if (root.TryGetProperty("rateLimits", out var limitsElement) && limitsElement.ValueKind == JsonValueKind.Array)
                limits = limitsElement.EnumerateArray().Select(Window).OfType<UsageWindow>().ToList();

            long? contextTokens = null, contextWindow = null;
            double? contextPercent = null;
            if (root.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                contextTokens = Long(context, "tokens");
                contextWindow = Long(context, "window");
                contextPercent = Number(context, "percent");
            }

            decimal? cost = null;
            if (root.TryGetProperty("cost", out var costElement) && costElement.ValueKind == JsonValueKind.Object &&
                costElement.TryGetProperty("usd", out var usd) && usd.ValueKind == JsonValueKind.Number &&
                usd.TryGetDecimal(out var usdValue))
                cost = usdValue;

            FeedAttention? attention = null;
            if (root.TryGetProperty("attention", out var attentionElement))
                attention = attentionElement.ValueKind switch
                {
                    JsonValueKind.Object when String(attentionElement, "type") is { Length: > 0 } type =>
                        new FeedAttention(Cut(type, 32), String(attentionElement, "tool") is { Length: > 0 } tool ? Cut(tool, 64) : null),
                    JsonValueKind.String when attentionElement.GetString() is { Length: > 0 } type => new FeedAttention(Cut(type, 32), null),
                    _ => null
                };
            TimeSpan? lastTurn = Number(root, "lastTurnMs") is { } turnMs && turnMs >= 0 && turnMs < TimeSpan.FromDays(30).TotalMilliseconds
                ? TimeSpan.FromMilliseconds(turnMs)
                : null;

            return ReadResult.Of(new FeedSession(
                sessionId,
                String(root, "surface"),
                String(root, "cwd"),
                String(root, "model"),
                String(root, "claudeCodeVersion"),
                String(root, "pluginVersion"),
                observedAt,
                Time(root, "startedAt"),
                Time(root, "rateLimitsAt"),
                limits,
                contextTokens,
                contextWindow,
                contextPercent,
                cost,
                String(root, "state"),
                Time(root, "stateSince"),
                attention,
                lastTurn));
        }
    }

    /// <summary>One <c>{ kind, percentUsed, resetsAt }</c> entry, or null when
    /// it lacks a kind or a percentage.</summary>
    private static UsageWindow? Window(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || String(element, "kind") is not { Length: > 0 } kind ||
            Number(element, "percentUsed") is not { } percent)
            return null;
        TimeSpan? length = kind switch
        {
            UsageWindowKinds.FiveHour => FiveHours,
            UsageWindowKinds.SevenDay or UsageWindowKinds.SevenDayOpus or UsageWindowKinds.SevenDaySonnet => SevenDays,
            _ => null
        };
        return new UsageWindow(kind, percent, Time(element, "resetsAt"), length);
    }

    /// <summary>The snapshot the layouts read, from the session records.</summary>
    internal static UsageSnapshot BuildSnapshot(IReadOnlyList<FeedSession> sessions, DateTimeOffset now)
    {
        if (sessions.Count == 0)
            return new UsageSnapshot { IsStale = true, FetchedAt = now, StatusDetail = WaitingDetail };

        var withLimits = sessions.Where(s => s.RateLimits is { Count: > 0 })
            .MaxBy(s => s.RateLimitsAt ?? s.ObservedAt);
        if (withLimits is null)
        {
            var newest = sessions.Max(s => s.ObservedAt);
            return new UsageSnapshot { IsStale = true, FetchedAt = newest > now ? now : newest, StatusDetail = NoLimitsDetail };
        }

        // Ages are measured on the widget's clock; a record from a clock that
        // runs ahead reads as "just now", never as from the future.
        var reportedAt = withLimits.RateLimitsAt ?? withLimits.ObservedAt;
        var snapshot = new UsageSnapshot { FetchedAt = reportedAt > now ? now : reportedAt };
        var others = new List<UsageWindow>();
        foreach (var window in withLimits.RateLimits!)
        {
            snapshot = window.Kind switch
            {
                UsageWindowKinds.FiveHour => snapshot with { FiveHourUtilizationPercent = window.PercentUsed, FiveHourResetsAt = window.ResetsAt },
                UsageWindowKinds.SevenDay => snapshot with { WeeklyUtilizationPercent = window.PercentUsed, WeeklyResetsAt = window.ResetsAt },
                UsageWindowKinds.SevenDayOpus => snapshot with { WeeklyOpusUtilizationPercent = window.PercentUsed },
                UsageWindowKinds.SevenDaySonnet => snapshot with { WeeklySonnetUtilizationPercent = window.PercentUsed },
                UsageWindowKinds.SpendLimit => snapshot with
                {
                    HasMonthlySpend = true,
                    MonthlySpendPercentOnly = true,
                    MonthlySpendPercent = window.PercentUsed,
                    MonthlySpendResetsAt = window.ResetsAt
                },
                _ => Keep(snapshot, others, window)
            };
        }
        return snapshot with
        {
            OtherWindows = others,
            Mode = snapshot.HasMonthlySpend ? UsageMode.MonthlySpend : UsageMode.PlanLimits
        };

        static UsageSnapshot Keep(UsageSnapshot snapshot, List<UsageWindow> others, UsageWindow window)
        {
            others.Add(window);
            return snapshot;
        }
    }

    private void CleanUpIfDue(DateTimeOffset now)
    {
        if (now - _lastCleanup < CleanupInterval)
            return;
        _lastCleanup = now;

        var removed = 0;
        foreach (var (path, entry) in _files.ToList())
        {
            var writtenAt = entry.Session?.ObservedAt ?? new DateTimeOffset(entry.LastWriteUtc, TimeSpan.Zero);
            var isOld = now - writtenAt > KeepFor || (entry.Session?.HasEnded == true && now - writtenAt > KeepEndedFor);
            if (!isOld)
                continue;
            try
            {
                File.Delete(path);
                _files.Remove(path);
                _loggedProblems.Remove(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogOnce("delete:" + path, $"Feed: could not delete {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        if (removed > 0)
            Log($"Feed: removed {removed} old session record(s).");
    }

    private void StartWatching()
    {
        try
        {
            _debounce ??= new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
            System.IO.Directory.CreateDirectory(_directory);
            var watcher = new FileSystemWatcher(_directory, FilePattern)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            watcher.Created += (_, _) => Signal();
            watcher.Changed += (_, _) => Signal();
            watcher.Deleted += (_, _) => Signal();
            watcher.Renamed += (_, _) => Signal();
            // The folder was deleted, or the watcher's buffer overflowed: the
            // next read starts a new watcher.
            watcher.Error += (_, e) =>
            {
                OnWatcherFailed();
                DiagnosticLog.WriteError("Feed watcher", e.GetException());
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watcherFailed = false;
            _loggedProblems.Remove("watch");
        }
        catch (Exception ex)
        {
            // The 15-second tick still reads the folder; only the instant
            // refresh is lost until a later read manages to watch again.
            LogOnce("watch", $"Feed: could not watch {_directory}: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>What the watcher's Error event does; tests call it because a
    /// real watcher failure can't be caused on demand.</summary>
    internal void OnWatcherFailed() => _watcherFailed = true;

    /// <summary>Called under the read lock, before each read.</summary>
    private void RestartWatcherIfItFailed()
    {
        if (!_watch || _disposed || (_watcher is not null && !_watcherFailed))
            return;
        _watcher?.Dispose();
        _watcher = null;
        StartWatching();
    }

    /// <summary>A burst of writes (the plugin rewrites a record several times
    /// per turn) becomes one <see cref="Changed"/>.</summary>
    private void Signal()
    {
        try { _debounce?.Change(ChangeDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void RaiseChanged()
    {
        if (_disposed)
            return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { DiagnosticLog.WriteError("Feed: Changed handler", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    private void LogOnce(string key, string message)
    {
        if (_loggedProblems.Add(key))
            Log(message);
    }

    private static string SessionIdFromFileName(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName) is var name && name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? name["claude-".Length..]
            : name;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static long? Long(JsonElement element, string name) =>
        Number(element, name) is { } number && number >= 0 && number <= long.MaxValue ? (long)number : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ParseTime(value) : null;

    /// <summary>A time as Claude Code or the plugin may spell it: milliseconds
    /// since the epoch (above 10^12), seconds since the epoch, or ISO 8601
    /// text (what <c>resetsAt</c> is in Claude Code 2.1.318–2.1.325).</summary>
    internal static DateTimeOffset? ParseTime(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out var number):
                return FromEpoch(number);
            case JsonValueKind.String:
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                    return FromEpoch(numeric);
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    private static DateTimeOffset? FromEpoch(double number)
    {
        if (double.IsNaN(number) || number <= 0)
            return null;
        var milliseconds = number > 1e12 ? number : number * 1000;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private sealed record Entry(DateTime LastWriteUtc, long Length, FeedSession? Session);

    internal enum ReadKind { Record, Refused, Broken }

    internal readonly record struct ReadResult(ReadKind Kind, FeedSession? Session, string Problem)
    {
        public static ReadResult Of(FeedSession session) => new(ReadKind.Record, session, "");
        public static ReadResult Refuse(string problem) => new(ReadKind.Refused, null, problem);
        public static ReadResult Broken(string problem) => new(ReadKind.Broken, null, problem);
    }
}
