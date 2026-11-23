using System.Text;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>Codex's limits from its session logs: the fixtures (hand-written from
/// the shapes in Codex's own source, rust-v0.170.0), which files are read, how
/// much of them, and that nothing outside <c>sessions/</c> is touched.</summary>
public class CodexUsageProviderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private DateTimeOffset _now = new(2026, 11, 19, 9, 5, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private string Home => _dir.Combine("codex");
    private string Sessions => Path.Combine(Home, "sessions");

    private static string FixtureText(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex", name));

    /// <summary>Puts text in <c>sessions/yyyy/MM/dd/rollout-….jsonl</c> with the given last write.</summary>
    private string Rollout(string day, string text, DateTime? writtenUtc = null, string? name = null)
    {
        var parts = day.Split('-');
        var folder = Path.Combine(Sessions, parts[0], parts[1], parts[2]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name ?? $"rollout-{day}T08-55-00-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, writtenUtc ?? _now.UtcDateTime.AddMinutes(-5));
        return path;
    }

    private CodexUsageProvider Provider(ICodexFileSystem? files = null) => new(Home, () => _now, files);

    private static string LimitLine(DateTimeOffset at, double fiveHour, double weekly, string? limitId = "codex") =>
        "{\"timestamp\":\"" + at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
        "Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"limit_id\":" +
        (limitId is null ? "null" : "\"" + limitId + "\"") +
        ",\"primary\":{\"used_percent\":" + fiveHour.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"window_minutes\":300,\"resets_at\":1795091400},\"secondary\":{\"used_percent\":" +
        weekly.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"window_minutes\":10080,\"resets_at\":1795500000},\"plan_type\":\"pro\"}}}";

    private static string Filler(int bytes)
    {
        var line = """{"timestamp":"2026-11-19T08:56:00.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"lorem ipsum dolor sit amet consectetur"}]}}""";
        var builder = new StringBuilder();
        while (builder.Length < bytes)
            builder.Append(line).Append('\n');
        return builder.ToString();
    }

    [Fact]
    public void The_current_shape_gives_both_windows_the_plan_and_the_time_of_the_event()
    {
        Rollout("2026-11-19", FixtureText("current.jsonl"));

        var usage = Provider().Read();

        Assert.Equal("codex", usage.ProviderId);
        Assert.Equal("Codex", usage.DisplayName);
        Assert.Equal("Plus", usage.PlanName);
        Assert.Equal(SourceTrust.Derived, usage.Trust);
        Assert.Equal(CodexUsageProvider.SourceIdValue, usage.SourceId);
        Assert.Equal(UsageHealth.Ok, usage.Health);
        Assert.True(usage.WholePointsOnly);
        Assert.Equal("from Codex's local session log", usage.SourceLabel);
        // The newest event of Codex's own bucket wins (43/18 at 09:00:00.5, not 41/17).
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 9, 0, 0, 500, TimeSpan.Zero), usage.ObservedAt);
        var fiveHour = usage.Window(UsageWindowKinds.FiveHour)!;
        Assert.Equal(43, fiveHour.PercentUsed);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 12, 30, 0, TimeSpan.Zero), fiveHour.ResetsAt);
        Assert.Equal(TimeSpan.FromHours(5), fiveHour.Length);
        var weekly = usage.Window(UsageWindowKinds.SevenDay)!;
        Assert.Equal(18, weekly.PercentUsed);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 6, 0, 0, TimeSpan.Zero), weekly.ResetsAt);
        Assert.Equal(TimeSpan.FromDays(7), weekly.Length);
        Assert.Equal("5-hour window", usage.WindowLabels[UsageWindowKinds.FiveHour]);
        Assert.Equal("Weekly", usage.WindowLabels[UsageWindowKinds.SevenDay]);
    }

    [Fact]
    public void Another_limit_bucket_comes_after_codex_s_own_under_its_name()
    {
        Rollout("2026-11-19", FixtureText("current.jsonl"));

        var usage = Provider().Read();

        Assert.Equal([UsageWindowKinds.FiveHour, UsageWindowKinds.SevenDay, "codex_other:five_hour"],
            usage.Windows.Select(w => w.Kind));
        Assert.Equal(12, usage.Window("codex_other:five_hour")!.PercentUsed);
        Assert.Equal("codex-other · 5-hour window", usage.WindowLabels["codex_other:five_hour"]);
    }

    [Fact]
    public void The_older_resets_in_seconds_shape_counts_from_the_event_s_time()
    {
        _now = new DateTimeOffset(2025, 9, 20, 10, 5, 0, TimeSpan.Zero);
        Rollout("2025-09-20", FixtureText("older-resets-in-seconds.jsonl"));

        var usage = Provider().Read();

        var at = new DateTimeOffset(2025, 9, 20, 10, 4, 0, TimeSpan.Zero);
        // 299 and 10079 minutes are within Codex's own 5% of 5 hours and a week.
        Assert.Equal(64, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
        Assert.Equal(at.AddSeconds(5400), usage.Window(UsageWindowKinds.FiveHour)!.ResetsAt);
        Assert.Equal(at.AddSeconds(259200), usage.Window(UsageWindowKinds.SevenDay)!.ResetsAt);
        Assert.Null(usage.PlanName);
    }

    [Fact]
    public void Events_without_limits_say_codex_does_not_report_them_in_this_mode()
    {
        Rollout("2026-11-19", FixtureText("null-limits.jsonl"));

        var usage = Provider().Read();

        Assert.Equal(UsageHealth.Unavailable, usage.Health);
        Assert.Empty(usage.Windows);
        Assert.Equal(CodexUsageProvider.NoLimitsDetail, usage.HealthDetail);
    }

    [Fact]
    public void A_folder_without_any_event_says_the_limits_come_with_codex_s_next_answer()
    {
        Directory.CreateDirectory(Sessions);

        var usage = Provider().Read();

        Assert.Equal(UsageHealth.Unavailable, usage.Health);
        Assert.Equal(CodexUsageProvider.NothingYetDetail, usage.HealthDetail);
    }

    [Fact]
    public void A_plan_without_a_five_hour_window_shows_only_the_weekly_one()
    {
        Rollout("2026-11-19", FixtureText("single-window.jsonl"));

        var usage = Provider().Read();

        var window = Assert.Single(usage.Windows);
        Assert.Equal(UsageWindowKinds.SevenDay, window.Kind);
        Assert.Equal(72, window.PercentUsed);
        Assert.Equal("Free", usage.PlanName);
    }

    [Fact]
    public void A_broken_window_loses_only_that_window()
    {
        var lines = FixtureText("malformed.jsonl").Split('\n');

        // "primary" is a string, "info" is a string, "plan_type" a number: the weekly window survives.
        var first = CodexUsageProvider.ParseLine(lines[1])!;
        Assert.Null(first.Primary);
        Assert.Equal(30.5, first.Secondary!.UsedPercent);
        Assert.Equal(10080, first.Secondary.WindowMinutes);
        Assert.Null(first.Plan);
        // A cut line is skipped.
        Assert.Null(CodexUsageProvider.ParseLine(lines[2]));
        // No used_percent: no primary; a length and a reset that aren't numbers: unknown, the percent stays.
        var last = CodexUsageProvider.ParseLine(lines[3])!;
        Assert.Null(last.Primary);
        Assert.Equal(31, last.Secondary!.UsedPercent);
        Assert.Null(last.Secondary.WindowMinutes);
        Assert.Null(last.Secondary.ResetsAt);
    }

    [Fact]
    public void A_file_with_broken_lines_still_gives_its_newest_good_event()
    {
        Rollout("2026-11-19", FixtureText("malformed.jsonl"));

        var usage = Provider().Read();

        var window = Assert.Single(usage.Windows);
        Assert.Equal("secondary_window", window.Kind);
        Assert.Equal(31, window.PercentUsed);
        Assert.Null(window.ResetsAt);
        Assert.Equal("Secondary limit", usage.WindowLabels["secondary_window"]);
    }

    [Fact]
    public void A_reading_older_than_ten_minutes_is_stale_and_says_it_updates_when_codex_runs()
    {
        Rollout("2026-11-19", FixtureText("current.jsonl"));
        _now = new DateTimeOffset(2026, 11, 19, 9, 10, 0, TimeSpan.Zero);
        Assert.Equal(UsageHealth.Ok, Provider().Read().Health);

        _now = new DateTimeOffset(2026, 11, 19, 9, 10, 1, TimeSpan.Zero);
        var usage = Provider().Read();

        Assert.Equal(UsageHealth.Stale, usage.Health);
        Assert.Equal(CodexUsageProvider.StaleDetail, usage.HealthDetail);
        Assert.Equal(43, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Fact]
    public void An_event_ahead_of_the_clock_is_read_as_now()
    {
        Rollout("2026-11-19", FixtureText("current.jsonl"));
        _now = new DateTimeOffset(2026, 11, 19, 8, 59, 0, TimeSpan.Zero);

        Assert.Equal(_now, Provider().Read().ObservedAt);
    }

    [Fact]
    public void The_newest_day_folder_wins_and_odd_names_and_compressed_files_are_left_alone()
    {
        Rollout("2026-11-18", LimitLine(_now.AddDays(-1), 90, 50) + "\n", _now.UtcDateTime.AddDays(-1));
        Rollout("2026-11-19", LimitLine(_now.AddMinutes(-2), 20, 30) + "\n");
        // Not a date folder, and a compressed rollout: neither is read.
        var notes = Path.Combine(Sessions, "note", "11", "19");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "rollout-x.jsonl"), LimitLine(_now, 99, 99));
        var zst = Path.Combine(Sessions, "2026", "11", "19", "rollout-2026-11-19T09-00-00-z.jsonl.zst");
        File.WriteAllText(zst, LimitLine(_now, 99, 99));

        var usage = Provider().Read();

        Assert.Equal(20, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Fact]
    public void A_newer_file_without_limits_falls_back_to_an_older_one_with_them()
    {
        Rollout("2026-11-19", LimitLine(_now.AddMinutes(-30), 55, 25) + "\n", _now.UtcDateTime.AddMinutes(-30));
        Rollout("2026-11-19", FixtureText("null-limits.jsonl"), _now.UtcDateTime.AddMinutes(-1));

        var usage = Provider().Read();

        Assert.Equal(55, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Fact]
    public void Of_two_sessions_at_once_the_newer_event_wins_whatever_the_file_order()
    {
        // The file written last holds the older event (its last line is a message).
        Rollout("2026-11-19", LimitLine(_now.AddMinutes(-3), 40, 20) + "\n" + Filler(200), _now.UtcDateTime.AddMinutes(-1));
        Rollout("2026-11-19", LimitLine(_now.AddMinutes(-2), 44, 21) + "\n", _now.UtcDateTime.AddMinutes(-2));

        var usage = Provider().Read();

        Assert.Equal(44, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Fact]
    public void Older_day_folders_are_looked_at_only_when_the_last_three_have_no_limits()
    {
        Rollout("2026-11-13", LimitLine(_now.AddDays(-6), 10, 70) + "\n", _now.UtcDateTime.AddDays(-6));
        foreach (var day in new[] { "2026-11-17", "2026-11-18", "2026-11-19" })
            Rollout(day, FixtureText("null-limits.jsonl"));
        var files = new CountingFileSystem();

        var usage = Provider(files).Read();

        Assert.Equal(70, usage.Window(UsageWindowKinds.SevenDay)!.PercentUsed);

        // With limits in the newest folder, the older ones aren't even listed.
        Rollout("2026-11-19", LimitLine(_now.AddMinutes(-1), 5, 71) + "\n", _now.UtcDateTime);
        files.Listed.Clear();
        Assert.Equal(71, Provider(files).Read().Window(UsageWindowKinds.SevenDay)!.PercentUsed);
        Assert.DoesNotContain(files.Listed, p => p.EndsWith(Path.Combine("11", "13"), StringComparison.Ordinal));
    }

    [Fact]
    public void No_more_than_eight_day_folders_are_looked_at()
    {
        Rollout("2026-11-01", LimitLine(_now.AddDays(-18), 10, 70) + "\n", _now.UtcDateTime.AddDays(-18));
        for (var day = 10; day <= 19; day++)
            Rollout($"2026-11-{day}", FixtureText("null-limits.jsonl"));

        var usage = Provider().Read();

        Assert.Equal(UsageHealth.Unavailable, usage.Health);
        Assert.Equal(CodexUsageProvider.NoLimitsDetail, usage.HealthDetail);
    }

    [Fact]
    public void Folders_of_another_month_and_year_are_ordered_by_date()
    {
        Rollout("2025-12-31", LimitLine(_now.AddDays(-300), 80, 80) + "\n", _now.UtcDateTime.AddDays(-300));
        Rollout("2026-01-02", LimitLine(_now.AddDays(-299), 33, 33) + "\n", _now.UtcDateTime.AddDays(-299));

        Assert.Equal(33, Provider().Read().Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Fact]
    public void A_large_file_is_read_from_its_last_256_kb()
    {
        var path = Rollout("2026-11-19", Filler(1_000_000) + LimitLine(_now.AddMinutes(-1), 61, 31) + "\n");
        var files = new CountingFileSystem();

        var usage = Provider(files).Read();

        Assert.Equal(61, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
        Assert.Equal(CodexUsageProvider.TailBytes, files.BytesRead[path]);
    }

    [Fact]
    public void An_event_further_back_than_256_kb_is_found_by_reading_more()
    {
        var path = Rollout("2026-11-19", LimitLine(_now.AddMinutes(-9), 62, 32) + "\n" + Filler(600_000));
        var files = new CountingFileSystem();

        var usage = Provider(files).Read();

        Assert.Equal(62, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
        Assert.Equal(CodexUsageProvider.TailBytes + new FileInfo(path).Length, files.BytesRead[path]);
    }

    [Fact]
    public void A_long_run_whose_newest_events_have_no_limits_is_read_from_its_tail_only()
    {
        // codex exec with an API key: limits early on (another mode), then events without them.
        var nullEvents = string.Join("\n", FixtureText("null-limits.jsonl").Split('\n').Skip(1));
        var path = Rollout("2026-11-19", LimitLine(_now.AddMinutes(-50), 30, 10) + "\n" + Filler(600_000) + nullEvents);
        var files = new CountingFileSystem();

        var usage = Provider(files).Read();

        Assert.Equal(CodexUsageProvider.NoLimitsDetail, usage.HealthDetail);
        Assert.Equal(CodexUsageProvider.TailBytes, files.BytesRead[path]);
    }

    [Fact]
    public void An_unchanged_file_is_not_read_again_and_a_changed_one_is()
    {
        var path = Rollout("2026-11-19", LimitLine(_now.AddMinutes(-4), 50, 20) + "\n");
        var files = new CountingFileSystem();
        var provider = Provider(files);

        provider.Read();
        provider.Read();
        Assert.Equal(1, files.Reads[path]);

        File.AppendAllText(path, LimitLine(_now.AddMinutes(-1), 52, 21) + "\n");
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime);
        Assert.Equal(52, provider.Read().Window(UsageWindowKinds.FiveHour)!.PercentUsed);
        Assert.Equal(2, files.Reads[path]);
    }

    [Fact]
    public void A_missing_bucket_id_is_codex_s_own_limit()
    {
        Rollout("2026-11-19", LimitLine(_now.AddMinutes(-1), 47, 19, limitId: null) + "\n");

        var usage = Provider().Read();

        Assert.Equal([UsageWindowKinds.FiveHour, UsageWindowKinds.SevenDay], usage.Windows.Select(w => w.Kind));
        Assert.Equal("Pro", usage.PlanName);
    }

    [Fact]
    public void An_event_without_a_bucket_id_and_a_codex_one_are_the_same_limit()
    {
        // Older builds wrote no limit_id; a newer event of the same session says "codex".
        Rollout("2026-11-19", LimitLine(_now.AddMinutes(-9), 90, 40, limitId: null) + "\n" +
                              LimitLine(_now.AddMinutes(-1), 20, 41) + "\n");

        var usage = Provider().Read();

        Assert.Equal([UsageWindowKinds.FiveHour, UsageWindowKinds.SevenDay], usage.Windows.Select(w => w.Kind));
        Assert.Equal(20, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Fact]
    public void Nothing_outside_the_sessions_folder_is_touched()
    {
        // Codex keeps its sign-in and settings beside the sessions folder: never read.
        Directory.CreateDirectory(Home);
        File.WriteAllText(Path.Combine(Home, "auth.json"), """{"tokens":{"access_token":"not-for-us"}}""");
        File.WriteAllText(Path.Combine(Home, "config.toml"), "model = \"gpt-5.5-codex\"");
        Rollout("2026-11-19", FixtureText("current.jsonl"));
        Rollout("2026-11-18", FixtureText("null-limits.jsonl"));
        var guarded = new GuardedFileSystem(Sessions);
        var provider = Provider(guarded);

        Assert.True(provider.IsPresent);
        Assert.Equal(43, provider.Read().Window(UsageWindowKinds.FiveHour)!.PercentUsed);
        Assert.NotEmpty(guarded.Touched);
        Assert.All(guarded.Touched, path => Assert.StartsWith(Sessions, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Without_codex_the_provider_is_absent_and_reads_nothing()
    {
        var guarded = new GuardedFileSystem(Sessions);
        var provider = Provider(guarded);

        Assert.False(provider.IsPresent);
        Assert.Empty(provider.Read().Windows);
        Assert.All(guarded.Touched, path => Assert.Equal(Sessions, path));
    }

    [Theory]
    [InlineData(300, false, "five_hour", "5-hour window")]
    [InlineData(285, false, "five_hour", "5-hour window")]
    [InlineData(284, false, "284_minute_window", "284-minute window")]
    [InlineData(1440, true, "daily_window", "Daily")]
    [InlineData(10080, true, "seven_day", "Weekly")]
    [InlineData(10584, true, "seven_day", "Weekly")]
    [InlineData(43200, true, "monthly_window", "Monthly")]
    [InlineData(525600, true, "annual_window", "Annual")]
    [InlineData(120, false, "2_hour_window", "2-hour window")]
    [InlineData(null, false, "primary_window", "Primary limit")]
    [InlineData(null, true, "secondary_window", "Secondary limit")]
    public void Windows_are_named_by_their_length_as_codex_names_them(int? minutes, bool secondary, string kind, string label)
    {
        Assert.Equal((kind, label), CodexUsageProvider.KindOf(minutes, secondary));
    }

    [Theory]
    [InlineData("plus", "Plus")]
    [InlineData("pro", "Pro")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("self_serve_business", "Self Serve Business")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Plan_names_read_as_words(string? plan, string? expected)
    {
        Assert.Equal(expected, CodexUsageProvider.PlanName(plan));
    }

    [Fact]
    public void Codex_s_folder_follows_codex_home()
    {
        var codexHome = _dir.Combine("elsewhere");
        Directory.CreateDirectory(codexHome);
        var paths = AppPaths.Resolve(name => name == AppPaths.CodexHomeVariable ? codexHome : null, [], isMac: false);

        Assert.Equal(Path.Combine(codexHome, "sessions"), new CodexUsageProvider(paths.CodexDirectory, () => _now).SessionsDirectory);
    }

    /// <summary>The disk, counting what is listed and read.</summary>
    private sealed class CountingFileSystem : ICodexFileSystem
    {
        private readonly ICodexFileSystem _disk = new GuardedFileSystem(null);
        public List<string> Listed { get; } = [];
        public Dictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> BytesRead { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool DirectoryExists(string path) => _disk.DirectoryExists(path);

        public IEnumerable<string> EnumerateDirectories(string path)
        {
            Listed.Add(path);
            return _disk.EnumerateDirectories(path);
        }

        public IEnumerable<string> EnumerateFiles(string path)
        {
            Listed.Add(path);
            return _disk.EnumerateFiles(path);
        }

        public CodexFileInfo? GetInfo(string path) => _disk.GetInfo(path);

        public byte[] ReadRange(string path, long offset, int count)
        {
            var bytes = _disk.ReadRange(path, offset, count);
            Reads[path] = Reads.GetValueOrDefault(path) + 1;
            BytesRead[path] = BytesRead.GetValueOrDefault(path) + bytes.Length;
            return bytes;
        }
    }
}

/// <summary>The disk, refusing (and recording) every path outside <paramref name="root"/>.</summary>
public sealed class GuardedFileSystem(string? root) : ICodexFileSystem
{
    public List<string> Touched { get; } = [];

    private string Check(string path)
    {
        Touched.Add(path);
        if (root is not null && !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Read outside {root}: {path}");
        return path;
    }

    public bool DirectoryExists(string path) => Directory.Exists(Check(path));

    public IEnumerable<string> EnumerateDirectories(string path) =>
        Directory.Exists(Check(path)) ? Directory.GetDirectories(path) : [];

    public IEnumerable<string> EnumerateFiles(string path) =>
        Directory.Exists(Check(path)) ? Directory.GetFiles(path) : [];

    public CodexFileInfo? GetInfo(string path)
    {
        var info = new FileInfo(Check(path));
        return info.Exists ? new CodexFileInfo(info.Length, info.LastWriteTimeUtc) : null;
    }

    public byte[] ReadRange(string path, long offset, int count)
    {
        using var stream = new FileStream(Check(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
