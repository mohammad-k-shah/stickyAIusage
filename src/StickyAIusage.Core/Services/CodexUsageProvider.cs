using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// OpenAI Codex's plan limits, read from the session logs Codex CLI writes
/// itself: <c>&lt;CODEX_HOME&gt;/sessions/YYYY/MM/DD/rollout-*.jsonl</c>, one
/// JSON record per line. After each model response Codex appends a
/// <c>token_count</c> event whose <c>rate_limits</c> carries the account's
/// windows (<c>primary</c>, <c>secondary</c>: <c>used_percent</c>,
/// <c>window_minutes</c>, <c>resets_at</c> in Unix seconds; older builds wrote
/// <c>resets_in_seconds</c>). No token, no network, nothing outside the
/// sessions folder. The format is Codex's internal one (its source is the
/// reference, not a documented interface), so every field is optional and
/// parsed on its own, and the reading is labelled <see cref="SourceTrust.Derived"/>.
/// Files are read from the end (256 KB first), and a file that didn't change
/// isn't read again. Never throws.
/// </summary>
public sealed class CodexUsageProvider : IProviderUsageReader
{
    public const string Id = "codex";
    public const string SourceIdValue = "codex-session-log";
    public const string SourceLabelText = "from Codex's local session log";

    /// <summary>A reading older than this says "as of": Codex writes one only while it works.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public const string StaleDetail = "Updates when Codex runs.";
    public const string NoLimitsDetail = "Codex does not report limits in this mode.";
    public const string NothingYetDetail = "No Codex limits yet: they appear after Codex's next answer.";

    /// <summary>Day folders looked through, newest first: three for a reading from
    /// the last days, up to eight so a weekly window used a few days ago still shows.</summary>
    internal const int RecentDayFolders = 3, MaxDayFolders = 8;

    /// <summary>At most this many files are read in one refresh (each only once while unchanged).</summary>
    internal const int MaxFilesPerRead = 24;

    internal const int TailBytes = 256 * 1024;
    internal const int WideTailBytes = 4 * 1024 * 1024;
    internal const long MaxScanBytes = 64L * 1024 * 1024;

    private readonly string _sessionsDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ICodexFileSystem _files;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, FileScan Scan)> _scans =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _failureLogged;

    /// <param name="codexDirectory">Codex's folder (<see cref="AppPaths.CodexDirectory"/>).</param>
    /// <param name="files">The file seam (tests guard and count every access); null: the disk.</param>
    public CodexUsageProvider(string codexDirectory, Func<DateTimeOffset> clock, ICodexFileSystem? files = null)
    {
        _sessionsDirectory = Path.Combine(codexDirectory, "sessions");
        _clock = clock;
        _files = files ?? PhysicalCodexFileSystem.Instance;
    }

    public string ProviderId => Id;

    public string DisplayName => "Codex";

    /// <summary>The only folder this provider reads.</summary>
    public string SessionsDirectory => _sessionsDirectory;

    /// <summary>Codex is on this machine: its sessions folder exists.</summary>
    public bool IsPresent
    {
        get
        {
            try
            {
                return _files.DirectoryExists(_sessionsDirectory);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Read());

    /// <summary>The newest limits Codex wrote, mapped to windows.</summary>
    public ProviderUsage Read()
    {
        var now = _clock();
        try
        {
            lock (_lock)
                return Map(Newest(), now);
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
                DiagnosticLog.WriteError("CodexUsageProvider.Read", ex);
            _failureLogged = true;
            return Empty(now, NothingYetDetail);
        }
    }

    /// <summary>One <c>token_count</c> event's limits: one bucket (Codex's own
    /// limit, or another named one), its two windows and the plan.</summary>
    internal sealed record LimitEvent(DateTimeOffset At, string Bucket, string? BucketName, string? Plan,
        CodexWindow? Primary, CodexWindow? Secondary);

    /// <summary><paramref name="ResetsAt"/> null: Codex didn't say.</summary>
    internal sealed record CodexWindow(double UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt);

    /// <summary>What one file holds: the newest event per bucket, and whether it
    /// had <c>token_count</c> events without limits (an API key, <c>codex exec</c>).</summary>
    internal sealed record FileScan(IReadOnlyDictionary<string, LimitEvent> Newest, bool SawEventsWithoutLimits);

    private sealed record Reading(IReadOnlyDictionary<string, LimitEvent> Buckets, bool SawEventsWithoutLimits);

    /// <summary>The newest event of each bucket across the recent files. Files are
    /// visited by last write, newest first, and only while a file could still hold
    /// something newer than what was found (two Codex sessions at once).</summary>
    private Reading Newest()
    {
        var buckets = new Dictionary<string, LimitEvent>(StringComparer.Ordinal);
        var sawWithoutLimits = false;
        if (!_files.DirectoryExists(_sessionsDirectory))
            return new Reading(buckets, false);

        var folders = DayFolders();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var read = 0;
        foreach (var batch in new[] { folders.Take(RecentDayFolders), folders.Skip(RecentDayFolders) })
        {
            var files = batch.SelectMany(RolloutFiles)
                .Select(path => (Path: path, Info: _files.GetInfo(path)))
                .Where(f => f.Info is not null)
                .OrderByDescending(f => f.Info!.Value.WrittenUtc)
                .ToList();
            foreach (var (path, info) in files)
            {
                seen.Add(path);
                var newestFound = buckets.Count == 0 ? (DateTimeOffset?)null : buckets.Values.Max(e => e.At);
                // A file last written before the newest event found can't hold a newer one.
                if (newestFound is { } found && new DateTimeOffset(info!.Value.WrittenUtc, TimeSpan.Zero) < found)
                    break;
                if (read++ >= MaxFilesPerRead)
                    break;
                var scan = Scan(path, info!.Value);
                sawWithoutLimits |= scan.SawEventsWithoutLimits;
                foreach (var (bucket, ev) in scan.Newest)
                    if (!buckets.TryGetValue(bucket, out var known) || ev.At > known.At)
                        buckets[bucket] = ev;
            }
            if (buckets.Count > 0)
                break;
        }
        // Forget files that are gone or no longer recent.
        foreach (var stale in _scans.Keys.Where(k => !seen.Contains(k)).ToList())
            _scans.Remove(stale);
        return new Reading(buckets, sawWithoutLimits);
    }

    /// <summary>The newest day folders (<c>YYYY/MM/DD</c>), newest first.</summary>
    private List<string> DayFolders()
    {
        var days = new List<string>();
        foreach (var year in Children(_sessionsDirectory, 4))
            foreach (var month in Children(year, 2))
                foreach (var day in Children(month, 2))
                {
                    days.Add(day);
                    if (days.Count == MaxDayFolders)
                        return days;
                }
        return days;
    }

    /// <summary>Sub-folders named with exactly <paramref name="digits"/> digits, highest first.</summary>
    private IEnumerable<string> Children(string folder, int digits) =>
        _files.EnumerateDirectories(folder)
            .Where(path => Path.GetFileName(path) is { } name && name.Length == digits && name.All(char.IsAsciiDigit))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal);

    /// <summary>Plain rollout files; Codex compresses week-old ones to <c>.jsonl.zst</c>, which are left alone.</summary>
    private IEnumerable<string> RolloutFiles(string folder) =>
        _files.EnumerateFiles(folder)
            .Where(path => Path.GetFileName(path) is { } name &&
                           name.StartsWith("rollout-", StringComparison.Ordinal) &&
                           name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));

    private FileScan Scan(string path, CodexFileInfo info)
    {
        if (_scans.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.WrittenUtc == info.WrittenUtc)
            return cached.Scan;
        var scan = ScanFile(path, info.Length);
        _scans[path] = (info.Length, info.WrittenUtc, scan);
        return scan;
    }

    /// <summary>Reads the file's end: 256 KB, then 4 MB, then up to 64 MB, until a
    /// <c>token_count</c> event turns up (a long session's last events are near the end).
    /// Events without limits stop it too: the newest events say this mode has none, and a
    /// long <c>codex exec</c> run isn't read whole on every refresh while it grows.</summary>
    private FileScan ScanFile(string path, long length)
    {
        FileScan scan = new(new Dictionary<string, LimitEvent>(), false);
        foreach (var size in new long[] { TailBytes, WideTailBytes, MaxScanBytes })
        {
            var take = Math.Min(length, size);
            var bytes = _files.ReadRange(path, length - take, (int)take);
            scan = ParseTail(bytes, wholeFile: take >= length);
            if (scan.Newest.Count > 0 || scan.SawEventsWithoutLimits || take >= length)
                break;
        }
        return scan;
    }

    /// <summary>The newest limit event per bucket in a run of JSONL bytes; the first
    /// line is skipped unless the run starts at the file's start (it may be cut).</summary>
    internal static FileScan ParseTail(byte[] bytes, bool wholeFile)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        var newest = new Dictionary<string, LimitEvent>(StringComparer.Ordinal);
        var sawWithoutLimits = false;
        for (var i = lines.Length - 1; i >= (wholeFile ? 0 : 1); i--)
        {
            var line = lines[i];
            // Most lines are messages and tool calls: only parse the events.
            if (!line.Contains("token_count", StringComparison.Ordinal))
                continue;
            switch (ParseLine(line))
            {
                case { } ev:
                    if (!newest.TryGetValue(ev.Bucket, out var known) || ev.At > known.At)
                        newest[ev.Bucket] = ev;
                    break;
                case null when IsTokenCount(line):
                    sawWithoutLimits = true;
                    break;
            }
        }
        return new FileScan(newest, sawWithoutLimits);
    }

    /// <summary>Codex's own limit bucket; others ("codex_other" and the like) have their own ids.</summary>
    internal const string MainBucket = "codex";

    /// <summary>One line → its limits, or null when it isn't a <c>token_count</c>
    /// event with limits. Each part is read on its own: a broken window loses
    /// only that window.</summary>
    internal static LimitEvent? ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                Str(root, "type") != "event_msg" ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                Str(payload, "type") != "token_count" ||
                !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
                return null;
            if (Str(root, "timestamp") is not { } stamp ||
                !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return null;
            var primary = Window(limits, "primary", at);
            var secondary = Window(limits, "secondary", at);
            if (primary is null && secondary is null)
                return null;
            var bucket = Str(limits, "limit_id") is { Length: > 0 } id ? id : MainBucket;
            return new LimitEvent(at, bucket, Str(limits, "limit_name"), Str(limits, "plan_type"), primary, secondary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTokenCount(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                   Str(payload, "type") == "token_count";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CodexWindow? Window(JsonElement limits, string name, DateTimeOffset at)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return null;
        if (Number(w, "used_percent") is not { } used || double.IsNaN(used) || double.IsInfinity(used))
            return null;
        int? minutes = Number(w, "window_minutes") is { } m && m > 0 && m < int.MaxValue ? (int)Math.Round(m) : null;
        DateTimeOffset? resets = null;
        if (Number(w, "resets_at") is { } unix && unix > 0 && unix < 253402300799)
            resets = DateTimeOffset.FromUnixTimeSeconds((long)unix);
        else if (Number(w, "resets_in_seconds") is { } secs && secs >= 0 && secs < 100L * 365 * 86400)
            resets = at.AddSeconds(secs);
        return new CodexWindow(Math.Max(0, used), minutes, resets);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private ProviderUsage Map(Reading reading, DateTimeOffset now)
    {
        if (reading.Buckets.Count == 0)
            return Empty(now, reading.SawEventsWithoutLimits ? NoLimitsDetail : NothingYetDetail);

        var windows = new List<UsageWindow>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var main = reading.Buckets.GetValueOrDefault(MainBucket) ??
                   reading.Buckets.Values.OrderByDescending(e => e.At).First();
        foreach (var ev in new[] { main }.Concat(reading.Buckets.Values.Where(e => e != main).OrderBy(e => e.Bucket, StringComparer.Ordinal)))
        {
            var prefix = ev == main ? null : ev.Bucket;
            var bucketName = ev == main ? null : BucketTitle(ev);
            foreach (var (window, secondary) in new[] { (ev.Primary, false), (ev.Secondary, true) })
            {
                if (window is null)
                    continue;
                var (kind, label) = KindOf(window.WindowMinutes, secondary);
                if (prefix is not null)
                    (kind, label) = ($"{prefix}:{kind}", $"{bucketName} · {Lower(label)}");
                while (labels.ContainsKey(kind))
                    kind += "_2";
                labels[kind] = label;
                windows.Add(new UsageWindow(kind, window.UsedPercent, window.ResetsAt,
                    window.WindowMinutes is { } m ? TimeSpan.FromMinutes(m) : null));
            }
        }
        // Readings are never ahead of the clock (the journal would see a clock set back).
        var observed = reading.Buckets.Values.Max(e => e.At);
        if (observed > now)
            observed = now;
        var stale = now - observed > StaleAfter;
        return new ProviderUsage(Id, DisplayName, PlanName(main.Plan), windows, null, observed, SourceIdValue,
            SourceTrust.Derived, stale ? UsageHealth.Stale : UsageHealth.Ok, stale ? StaleDetail : null)
        {
            WholePointsOnly = true,
            SourceLabel = SourceLabelText,
            WindowLabels = labels
        };
    }

    private ProviderUsage Empty(DateTimeOffset now, string detail) =>
        new(Id, DisplayName, null, [], null, now, SourceIdValue, SourceTrust.Derived, UsageHealth.Unavailable, detail)
        {
            WholePointsOnly = true,
            SourceLabel = SourceLabelText
        };

    /// <summary>A window's kind and name from its length, as Codex's own
    /// <c>/status</c> names it (within 5%): 5 hours, a day, a week, a month, a year.</summary>
    internal static (string Kind, string Label) KindOf(int? minutes, bool secondary)
    {
        if (minutes is not { } m)
            return secondary ? ("secondary_window", "Secondary limit") : ("primary_window", "Primary limit");
        static bool Near(int value, int expected) => value >= expected * 0.95 && value <= expected * 1.05;
        if (Near(m, 300)) return (UsageWindowKinds.FiveHour, "5-hour window");
        if (Near(m, 1440)) return ("daily_window", "Daily");
        if (Near(m, 10080)) return (UsageWindowKinds.SevenDay, "Weekly");
        if (Near(m, 43200)) return ("monthly_window", "Monthly");
        if (Near(m, 525600)) return ("annual_window", "Annual");
        return m % 60 == 0
            ? ($"{m / 60}_hour_window", $"{m / 60}-hour window")
            : ($"{m}_minute_window", $"{m}-minute window");
    }

    private static string BucketTitle(LimitEvent ev) =>
        ev.BucketName is { Length: > 0 } name ? name : ev.Bucket;

    private static string Lower(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    /// <summary>"Plus", "Pro", "Business" from Codex's plan id; null when it didn't say.</summary>
    internal static string? PlanName(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return null;
        var words = plan.Trim().Split('_', '-', ' ').Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return string.Join(' ', words);
    }
}

/// <summary>Size and last write of a file.</summary>
public readonly record struct CodexFileInfo(long Length, DateTime WrittenUtc);

/// <summary>Everything <see cref="CodexUsageProvider"/> asks of the disk, so a test
/// can check it never reaches outside Codex's sessions folder.</summary>
public interface ICodexFileSystem
{
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFiles(string path);

    /// <summary>Null when the file is gone.</summary>
    CodexFileInfo? GetInfo(string path);

    /// <summary>Up to <paramref name="count"/> bytes from <paramref name="offset"/>;
    /// opened for reading while Codex keeps appending.</summary>
    byte[] ReadRange(string path, long offset, int count);
}

internal sealed class PhysicalCodexFileSystem : ICodexFileSystem
{
    public static readonly PhysicalCodexFileSystem Instance = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IEnumerable<string> EnumerateFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public CodexFileInfo? GetInfo(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new CodexFileInfo(info.Length, info.LastWriteTimeUtc) : null;
    }

    public byte[] ReadRange(string path, long offset, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, offset), SeekOrigin.Begin);
            var buffer = new byte[Math.Max(0, Math.Min(count, stream.Length - stream.Position))];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
