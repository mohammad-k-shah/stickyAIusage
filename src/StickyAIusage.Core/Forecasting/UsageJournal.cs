using System.Globalization;
using System.IO;
using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Forecasting;

/// <summary>
/// A small journal of usage readings, so a pace can be measured: every
/// window of every reading becomes a sample, kept in memory and appended to
/// <c>cache/journal.jsonl</c> (one JSON object per line, <c>"schema": 1</c>),
/// so a restart keeps the history. A reading that repeats the previous one -
/// the same observation read again on the next tick, or the same percentage
/// within five minutes - adds nothing. The file is rewritten (atomically) when
/// it passes 5,000 lines or holds samples past their age: eight days, 35 for
/// spend caps. Never throws: a broken line is skipped, a failed write logged.
/// Thread-safe.
/// </summary>
public sealed class UsageJournal
{
    public const int MaxLines = 5000;

    /// <summary>What a rewrite keeps when the file is full of samples still in date.</summary>
    internal const int KeepWhenFull = 4000;

    public static readonly TimeSpan RepeatEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(8);
    public static readonly TimeSpan KeepSpendFor = TimeSpan.FromDays(35);
    private static readonly TimeSpan PruneCheckEvery = TimeSpan.FromHours(1);

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<(string Provider, string Kind), List<UsageSample>> _series = new();
    private int _lines;
    private DateTimeOffset _lastPruneCheck = DateTimeOffset.MinValue;
    private bool _writeFailureLogged;
    private bool _loaded;
    private bool _rewriteDue;

    /// <summary>How far ahead of the clock a sample may be before the clock
    /// counts as set back (small skews between sources are normal).</summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(2);

    /// <summary>The file is read on first use, on the caller's (background)
    /// thread, not here: the app builds its services on the UI thread.</summary>
    public UsageJournal(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Every sample of one window kind, oldest first.</summary>
    public IReadOnlyList<UsageSample> Samples(string providerId, string kind)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _series.TryGetValue((providerId, kind), out var list) ? list.ToList() : [];
        }
    }

    /// <summary>Records each window of the reading; true when anything new was kept.</summary>
    public bool Record(ProviderUsage usage, Func<UsageWindow, DateTimeOffset?>? resetsAt = null)
    {
        var added = false;
        foreach (var window in usage.Windows)
            added |= Record(new UsageSample(usage.ProviderId, window.Kind, resetsAt?.Invoke(window) ?? window.ResetsAt,
                window.PercentUsed, usage.ObservedAt));
        return added;
    }

    /// <summary>Keeps the sample unless it repeats the previous one; true when kept.</summary>
    public bool Record(UsageSample sample)
    {
        if (!double.IsFinite(sample.PercentUsed))
            return false;
        lock (_lock)
        {
            EnsureLoaded();
            var key = (sample.ProviderId, sample.Kind);
            if (!_series.TryGetValue(key, out var list))
                _series[key] = list = [];
            // The clock was set back: samples "from the future" would make
            // every new reading look old and be refused until the clock
            // caught up. They go (and the file is rewritten).
            var now = _clock();
            if (list.Count > 0 && list[^1].ObservedAt > now + ClockSlack)
            {
                foreach (var series in _series.Values)
                    series.RemoveAll(s => s.ObservedAt > now + ClockSlack);
                _rewriteDue = true;
            }
            if (list.Count > 0)
            {
                var last = list[^1];
                // The same observation read again (a cached reading, a feed
                // record that didn't change), or an older one arriving late.
                if (sample.ObservedAt <= last.ObservedAt)
                    return false;
                if (sample.PercentUsed == last.PercentUsed && PaceCalculator.SameReset(sample.ResetsAt, last.ResetsAt) &&
                    sample.ObservedAt - last.ObservedAt < RepeatEvery)
                    return false;
            }
            list.Add(sample);
            Append(sample);
            PruneIfDue();
            return true;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            foreach (var line in File.ReadLines(_path))
            {
                _lines++;
                if (Parse(line) is not { } sample)
                    continue;
                var key = (sample.ProviderId, sample.Kind);
                if (!_series.TryGetValue(key, out var list))
                    _series[key] = list = [];
                list.Add(sample);
            }
            foreach (var list in _series.Values)
                list.Sort((a, b) => a.ObservedAt.CompareTo(b.ObservedAt));
            PruneIfDue();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageJournal.Load", ex);
        }
    }

    private void Append(UsageSample sample)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.AppendAllText(_path, Serialize(sample) + "\n");
            _lines++;
        }
        catch (Exception ex)
        {
            if (!_writeFailureLogged)
                DiagnosticLog.WriteError("UsageJournal.Append", ex);
            _writeFailureLogged = true;
        }
    }

    /// <summary>Drops samples past their age; rewrites the file when it shrank
    /// or grew past <see cref="MaxLines"/>. Checked at most once an hour,
    /// unless the file is full.</summary>
    private void PruneIfDue()
    {
        var now = _clock();
        if (!_rewriteDue && _lines <= MaxLines && now - _lastPruneCheck < PruneCheckEvery)
            return;
        _lastPruneCheck = now;

        var removed = 0;
        foreach (var (key, list) in _series)
            removed += list.RemoveAll(s => now - s.ObservedAt > (IsSpend(key.Kind) ? KeepSpendFor : KeepFor));

        var total = _series.Values.Sum(l => l.Count);
        if (total > MaxLines)
        {
            // Still too many in date: keep the newest across all windows.
            var cutoff = _series.Values.SelectMany(l => l).Select(s => s.ObservedAt)
                .OrderByDescending(t => t).ElementAt(KeepWhenFull - 1);
            foreach (var list in _series.Values)
                removed += list.RemoveAll(s => s.ObservedAt < cutoff);
        }
        foreach (var key in _series.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
            _series.Remove(key);

        if (removed == 0 && _lines <= MaxLines && !_rewriteDue)
            return;
        _rewriteDue = false;
        try
        {
            var lines = _series.Values.SelectMany(l => l).OrderBy(s => s.ObservedAt).Select(Serialize).ToList();
            AtomicFile.WriteAllText(_path, lines.Count == 0 ? "" : string.Join("\n", lines) + "\n");
            _lines = lines.Count;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageJournal.Compact", ex);
        }
    }

    private static bool IsSpend(string kind) => kind is UsageWindowKinds.MonthSpend or UsageWindowKinds.SpendLimit;

    internal static string Serialize(UsageSample s) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["schema"] = 1,
        ["provider"] = s.ProviderId,
        ["kind"] = s.Kind,
        ["resetsAt"] = s.ResetsAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["percent"] = Math.Round(s.PercentUsed, 3),
        ["at"] = s.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    });

    /// <summary>A sample from one line, or null for anything else (a cut-off
    /// line, another schema, missing fields).</summary>
    internal static UsageSample? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1 ||
                Text(root, "provider") is not { Length: > 0 } provider ||
                Text(root, "kind") is not { Length: > 0 } kind ||
                !root.TryGetProperty("percent", out var percent) || percent.ValueKind != JsonValueKind.Number ||
                Time(root, "at") is not { } at)
                return null;
            var value = percent.GetDouble();
            return double.IsFinite(value) ? new UsageSample(provider, kind, Time(root, "resetsAt"), value, at) : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement root, string name) =>
        Text(root, name) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
}
