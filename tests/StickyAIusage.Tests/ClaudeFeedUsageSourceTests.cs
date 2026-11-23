using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>The Claude Code plugin's feed: reading the records it writes,
/// turning them into a reading, and keeping the folder tidy.</summary>
public class ClaudeFeedUsageSourceTests : IDisposable
{
    /// <summary>observedAt of the fixtures (2026-11-17 04:53:56 UTC).</summary>
    private static readonly DateTimeOffset Written = DateTimeOffset.FromUnixTimeMilliseconds(1794881636736);

    private readonly TempDir _dir = new();
    private DateTimeOffset _now = Written.AddSeconds(30);
    private int _delays;

    public void Dispose() => _dir.Dispose();

    private string Feed => _dir.Combine("feed");

    private ClaudeFeedUsageSource Source(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(Feed, () => _now, delay ?? ((_, _) => { _delays++; return Task.CompletedTask; })) { Log = _log.Add };

    private string CopyFixture(string name, string? asName = null)
    {
        Directory.CreateDirectory(Feed);
        var target = Path.Combine(Feed, asName ?? name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "feed", name), target, overwrite: true);
        return target;
    }

    private string WriteRecord(string sessionId, DateTimeOffset observedAt, DateTimeOffset? rateLimitsAt,
        double? fiveHour, string state = "idle", DateTimeOffset? fiveHourResetsAt = null)
    {
        Directory.CreateDirectory(Feed);
        var path = Path.Combine(Feed, $"claude-{sessionId}.json");
        object? limits = fiveHour is { } percent
            ? new object[]
            {
                new { kind = "five_hour", percentUsed = percent, resetsAt = (fiveHourResetsAt ?? Written.AddHours(2)).ToString("O") },
                new { kind = "seven_day", percentUsed = 30, resetsAt = Written.AddDays(3).ToString("O") }
            }
            : Array.Empty<object>();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema = 1,
            sessionId,
            observedAt = observedAt.ToUnixTimeMilliseconds(),
            rateLimitsAt = rateLimitsAt?.ToUnixTimeMilliseconds(),
            rateLimits = limits,
            state
        }));
        // A rewrite within the file system's timestamp resolution must still
        // read as changed (its length may be the same).
        File.SetLastWriteTimeUtc(path, observedAt.UtcDateTime);
        return path;
    }

    private readonly List<string> _log = [];

    private int LogLinesMentioning(string text) => _log.Count(line => line.Contains(text));

    [Fact]
    public async Task A_record_from_Claude_Code_becomes_a_plan_limits_reading()
    {
        CopyFixture("claude-pro-cli.json");

        var snapshot = await Source().TryGetAsync(default);

        Assert.NotNull(snapshot);
        Assert.Equal(UsageMode.PlanLimits, snapshot.Mode);
        Assert.Equal(85, snapshot.FiveHourUtilizationPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 5, 10, 0, TimeSpan.Zero), snapshot.FiveHourResetsAt);
        Assert.Equal(61, snapshot.WeeklyUtilizationPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), snapshot.WeeklyResetsAt);
        Assert.False(snapshot.IsStale);
        Assert.False(snapshot.HasMonthlySpend);
        // As of when Claude Code reported the limits, not when the record was written.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794881635672), snapshot.FetchedAt);
    }

    [Fact]
    public async Task The_session_parts_of_a_record_are_kept_for_later_features()
    {
        CopyFixture("claude-pro-cli.json");
        var source = Source();

        await source.TryGetAsync(default);

        var session = Assert.Single(source.Sessions);
        Assert.Equal("9d5d7009-0d2b-4a35-a3f4-6c1d2e0a9b11", session.SessionId);
        Assert.Equal("terminal", session.Surface);
        Assert.Equal("2.1.325", session.ClaudeCodeVersion);
        Assert.Equal(38097, session.ContextTokens);
        Assert.Equal(1_000_000, session.ContextWindow);
        Assert.Equal(0.00245073m, session.CostUsd);
        Assert.Equal("idle", session.State);
        Assert.Null(session.Attention);
    }

    [Fact]
    public async Task A_record_caught_mid_write_is_read_again_and_otherwise_the_last_good_one_is_kept()
    {
        var id = "mid-write-" + Guid.NewGuid().ToString("N");
        var path = WriteRecord(id, Written, Written, fiveHour: 40);
        var source = Source();
        Assert.Equal(40, (await source.TryGetAsync(default))!.FiveHourUtilizationPercent);

        File.WriteAllText(path, "{\"schema\":1,\"rateLim");
        _delays = 0;
        var snapshot = await source.TryGetAsync(default);

        Assert.Equal(ClaudeFeedUsageSource.ParseRetries, _delays);
        Assert.Equal(40, snapshot!.FiveHourUtilizationPercent);
        Assert.Equal(1, LogLinesMentioning($"could not read claude-{id}.json"));
    }

    [Fact]
    public async Task A_record_that_completes_while_it_is_read_again_is_used()
    {
        var path = WriteRecord("s2", Written, Written, fiveHour: 40);
        var complete = File.ReadAllText(path).Replace("40", "55");
        File.WriteAllText(path, complete[..20]);
        var source = Source((_, _) =>
        {
            _delays++;
            if (_delays == 2)
                File.WriteAllText(path, complete);
            return Task.CompletedTask;
        });

        var snapshot = await source.TryGetAsync(default);

        Assert.Equal(55, snapshot!.FiveHourUtilizationPercent);
        Assert.Equal(2, _delays);
    }

    [Fact]
    public async Task A_record_of_an_unknown_schema_is_ignored_with_one_log_line()
    {
        var name = $"claude-future-{Guid.NewGuid():N}.json";
        CopyFixture("claude-schema-2.json", name);
        var source = Source();

        var first = await source.TryGetAsync(default);
        var second = await source.TryGetAsync(default);

        Assert.False(first!.HasReading);
        Assert.False(second!.HasReading);
        Assert.Empty(source.Sessions);
        Assert.Equal(1, LogLinesMentioning($"ignored {name}: unknown schema 2"));
    }

    [Theory]
    [InlineData("1794892436", 1794892436000L)]          // seconds
    [InlineData("1794892436000", 1794892436000L)]       // milliseconds
    [InlineData("\"2026-11-17T05:13:56Z\"", 1794892436000L)]
    [InlineData("\"2026-11-17T05:13:56.000Z\"", 1794892436000L)]
    [InlineData("\"2026-11-17T10:43:56+05:30\"", 1794892436000L)]
    [InlineData("\"1794892436\"", 1794892436000L)]      // a number written as text
    [InlineData("\"soon\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("null", null)]
    [InlineData("true", null)]
    public void Reset_times_are_read_as_seconds_milliseconds_or_ISO_text(string json, long? expectedMilliseconds)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = ClaudeFeedUsageSource.ParseTime(document.RootElement);

        Assert.Equal(expectedMilliseconds, parsed?.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Unknown_window_kinds_are_kept_and_malformed_entries_skipped()
    {
        CopyFixture("claude-unknown-kind.json");

        var snapshot = await Source().TryGetAsync(default);

        Assert.Equal(20.5, snapshot!.FiveHourUtilizationPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794885236), snapshot.FiveHourResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795400000000), snapshot.WeeklyResetsAt);
        var other = Assert.Single(snapshot.OtherWindows);
        Assert.Equal("seven_day_cowork", other.Kind);
        Assert.Equal(12, other.PercentUsed);
        Assert.Contains(snapshot.ToProviderUsage().Windows, w => w.Kind == "seven_day_cowork");
    }

    [Fact]
    public async Task With_several_sessions_the_limits_reported_last_win()
    {
        // s-old wrote last (a heartbeat), but s-new had the newer response.
        WriteRecord("s-old", Written, rateLimitsAt: Written.AddMinutes(-10), fiveHour: 40);
        WriteRecord("s-new", Written.AddMinutes(-2), rateLimitsAt: Written.AddMinutes(-2), fiveHour: 47);

        var snapshot = await Source().TryGetAsync(default);

        Assert.Equal(47, snapshot!.FiveHourUtilizationPercent);
        Assert.Equal(Written.AddMinutes(-2), snapshot.FetchedAt);
    }

    [Fact]
    public async Task A_new_session_without_limits_yet_keeps_the_previous_sessions_limits()
    {
        WriteRecord("s-before", Written.AddMinutes(-5), Written.AddMinutes(-5), fiveHour: 47);
        WriteRecord("s-just-started", Written, rateLimitsAt: null, fiveHour: null, state: "started");
        var source = Source();

        var snapshot = await source.TryGetAsync(default);

        Assert.Equal(47, snapshot!.FiveHourUtilizationPercent);
        Assert.Equal(2, source.Sessions.Count);
        Assert.Equal("s-just-started", source.Sessions[0].SessionId);
    }

    [Fact]
    public async Task Without_any_limits_the_reading_is_empty_and_says_why()
    {
        WriteRecord("api-key-user", Written, rateLimitsAt: null, fiveHour: null);

        var snapshot = await Source().TryGetAsync(default);

        Assert.False(snapshot!.HasReading);
        Assert.True(snapshot.IsStale);
        Assert.Equal(ClaudeFeedUsageSource.NoLimitsDetail, snapshot.StatusDetail);
    }

    [Fact]
    public async Task Without_a_feed_folder_the_reading_is_empty_and_says_how_to_connect()
    {
        var snapshot = await Source().TryGetAsync(default);

        Assert.False(snapshot!.HasReading);
        Assert.Equal(ClaudeFeedUsageSource.WaitingDetail, snapshot.StatusDetail);
        var view = WidgetViewModel.From(snapshot with { SourceId = ClaudeFeedUsageSource.Id }, null, new AppSettings(), _now);
        Assert.Equal(UsageMode.Unknown, view.Mode);
        Assert.Equal(UsageState.Unavailable, view.State);
        Assert.Equal(ClaudeFeedUsageSource.WaitingDetail, view.UsageTooltip);
    }

    [Fact]
    public async Task A_record_from_a_clock_that_runs_ahead_reads_as_now()
    {
        WriteRecord("ahead", Written.AddMinutes(5), Written.AddMinutes(5), fiveHour: 40);

        var snapshot = await Source().TryGetAsync(default);

        Assert.Equal(_now, snapshot!.FetchedAt);
    }

    [Fact]
    public async Task Old_and_long_ended_records_are_deleted()
    {
        var weekOld = WriteRecord("week-old", Written.AddDays(-8), Written.AddDays(-8), fiveHour: 10);
        var endedYesterday = WriteRecord("ended-yesterday", Written.AddDays(-2), Written.AddDays(-2), fiveHour: 10, state: "ended");
        var endedNow = WriteRecord("ended-now", Written.AddHours(-2), Written.AddHours(-2), fiveHour: 10, state: "ended");
        var live = WriteRecord("live", Written, Written, fiveHour: 20);

        var snapshot = await Source().TryGetAsync(default);

        Assert.False(File.Exists(weekOld));
        Assert.False(File.Exists(endedYesterday));
        Assert.True(File.Exists(endedNow));
        Assert.True(File.Exists(live));
        Assert.Equal(20, snapshot!.FiveHourUtilizationPercent);
    }

    [Fact]
    public async Task A_deleted_record_no_longer_counts()
    {
        var path = WriteRecord("gone", Written, Written, fiveHour: 40);
        var source = Source();
        await source.TryGetAsync(default);

        File.Delete(path);
        var snapshot = await source.TryGetAsync(default);

        Assert.False(snapshot!.HasReading);
        Assert.Empty(source.Sessions);
    }

    [Fact]
    public async Task Only_files_named_like_session_records_are_read()
    {
        Directory.CreateDirectory(Feed);
        File.WriteAllText(Path.Combine(Feed, "plugin.json"), "{\"schema\":1,\"pluginVersion\":\"2.0.0\"}");
        File.WriteAllText(Path.Combine(Feed, "notes.txt"), "hello");

        var snapshot = await Source().TryGetAsync(default);

        Assert.Equal(ClaudeFeedUsageSource.WaitingDetail, snapshot!.StatusDetail);
    }

    [Fact]
    public async Task The_watcher_raises_Changed_when_the_plugin_writes_a_record()
    {
        Directory.CreateDirectory(Feed);
        using var source = new ClaudeFeedUsageSource(Feed, () => _now, watch: true);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += () => changed.TrySetResult();

        WriteRecord("watched", Written, Written, fiveHour: 40);

        var finished = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(changed.Task, finished);
    }

    [Fact]
    public async Task A_record_left_cut_short_is_not_read_again_until_it_changes()
    {
        Directory.CreateDirectory(Feed);
        var path = Path.Combine(Feed, "claude-killed.json");
        File.WriteAllText(path, "{\"schema\":1,\"sess");
        var source = Source();

        await source.TryGetAsync(default);
        Assert.Equal(ClaudeFeedUsageSource.ParseRetries, _delays);

        _delays = 0;
        await source.TryGetAsync(default);
        Assert.Equal(0, _delays);

        WriteRecord("killed", Written, Written, fiveHour: 25);
        var snapshot = await source.TryGetAsync(default);
        Assert.Equal(25, snapshot!.FiveHourUtilizationPercent);
    }

    [Fact]
    public async Task A_record_left_cut_short_is_deleted_once_it_is_old()
    {
        Directory.CreateDirectory(Feed);
        var path = Path.Combine(Feed, "claude-abandoned.json");
        File.WriteAllText(path, "{\"schema\":1,\"sess");
        File.SetLastWriteTimeUtc(path, Written.AddDays(-8).UtcDateTime);

        await Source().TryGetAsync(default);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_failed_watcher_is_started_again_on_the_next_read()
    {
        using var source = new ClaudeFeedUsageSource(Feed, () => _now, watch: true) { Log = _log.Add };
        Directory.Delete(Feed, recursive: true);
        source.OnWatcherFailed();

        await source.TryGetAsync(default);

        Assert.True(Directory.Exists(Feed));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += () => changed.TrySetResult();
        WriteRecord("after-restart", Written, Written, fiveHour: 40);
        var finished = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(changed.Task, finished);
    }

    [Fact]
    public void Watching_creates_the_feed_folder()
    {
        using var source = new ClaudeFeedUsageSource(Feed, () => _now, watch: true);

        Assert.True(Directory.Exists(Feed));
    }
}

/// <summary>How a feed reading looks in the layouts.</summary>
public class FeedReadingViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot FeedReading(TimeSpan age) => new()
    {
        Mode = UsageMode.PlanLimits,
        FiveHourUtilizationPercent = 43,
        FiveHourResetsAt = Now.AddHours(2),
        WeeklyUtilizationPercent = 14,
        WeeklyResetsAt = Now.AddDays(3),
        FetchedAt = Now - age,
        SourceId = ClaudeFeedUsageSource.Id,
        Trust = SourceTrust.Official
    };

    [Fact]
    public void A_live_feed_reading_reads_as_updated()
    {
        var view = WidgetViewModel.From(FeedReading(TimeSpan.FromSeconds(90)), null, new AppSettings(), Now);

        Assert.Equal("Updated: 1m ago", view.UpdatedText);
        Assert.Equal("Updated 1m ago", view.SummaryUpdatedText);
    }

    [Fact]
    public void An_older_feed_reading_reads_as_of_its_time_and_says_when_it_updates()
    {
        var view = WidgetViewModel.From(FeedReading(TimeSpan.FromMinutes(12)), null, new AppSettings(), Now);

        Assert.Equal("As of: 12m ago", view.UpdatedText);
        Assert.Equal("As of 12m ago", view.SummaryUpdatedText);
        Assert.Contains("As of 12m ago - updates while Claude Code is active", view.UsageTooltip);
        Assert.DoesNotContain("retrying", view.UsageTooltip);
    }

    [Fact]
    public void An_endpoint_reading_of_the_same_age_keeps_its_wording()
    {
        var view = WidgetViewModel.From(FeedReading(TimeSpan.FromMinutes(12)) with { SourceId = "claude-oauth" },
            null, new AppSettings(), Now);

        Assert.Equal("Updated: 12m ago", view.UpdatedText);
    }

    [Fact]
    public void An_older_monthly_feed_reading_says_as_of_in_the_pill()
    {
        var reading = new UsageSnapshot
        {
            Mode = UsageMode.MonthlySpend,
            HasMonthlySpend = true,
            MonthlySpendPercentOnly = true,
            MonthlySpendPercent = 63.4,
            FetchedAt = Now.AddMinutes(-12),
            SourceId = ClaudeFeedUsageSource.Id
        };

        var view = WidgetViewModel.From(reading, null, new AppSettings(), Now);

        Assert.Equal("· as of 12m ago", view.StaleText);
    }

    [Fact]
    public void A_passed_reset_reads_as_a_new_window()
    {
        var reading = FeedReading(TimeSpan.FromMinutes(40)) with { FiveHourResetsAt = Now.AddMinutes(-5) };

        var view = WidgetViewModel.From(reading, null, new AppSettings(), Now);

        Assert.Equal("5h: 0%", view.PrimaryText);
        Assert.Equal("- not started", view.SecondaryCompactText);
    }

    [Fact]
    public async Task A_percent_only_spend_limit_shows_no_dollars_and_no_day_target()
    {
        using var dir = new TempDir();
        var feed = dir.Combine("feed");
        Directory.CreateDirectory(feed);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "feed", "claude-gateway-spend.json"),
            Path.Combine(feed, "claude-gateway-spend.json"));
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1794881636736).AddSeconds(30);
        var snapshot = (await new ClaudeFeedUsageSource(feed, () => now).TryGetAsync(default))!
            with { SourceId = ClaudeFeedUsageSource.Id };

        var view = WidgetViewModel.From(snapshot, null, new AppSettings(), now, spentToday: null);

        Assert.Equal(UsageMode.MonthlySpend, view.Mode);
        Assert.Equal("63%", view.PrimaryText);
        Assert.Equal("of limit", view.SecondaryText);
        Assert.Equal(63.4, view.RingPercent);
        Assert.False(view.ShowDayTarget);
        var row = Assert.Single(view.SummaryRows);
        Assert.Equal("Spend limit", row.Label);
        Assert.StartsWith("63% of your limit", row.Value);
        Assert.DoesNotContain("$", view.UsageTooltip + row.Value + view.PrimaryText + view.SecondaryText);

        var neutral = snapshot.ToProviderUsage();
        Assert.Null(neutral.Spend!.UsedUsd);
        Assert.Equal(63.4, neutral.Window(UsageWindowKinds.SpendLimit)!.PercentUsed);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), neutral.Spend.ResetsAt);
    }

    [Fact]
    public void A_window_of_an_unknown_kind_gets_a_flyout_row_by_name()
    {
        var reading = FeedReading(TimeSpan.Zero) with
        {
            OtherWindows = [new UsageWindow("seven_day_cowork", 12, Now.AddDays(2), null)]
        };

        var view = WidgetViewModel.From(reading, null, new AppSettings(), Now);

        var row = Assert.Single(view.SummaryRows, r => r.Label == "Seven day cowork");
        Assert.StartsWith("12% · ", row.Value);
        Assert.Contains("Seven day cowork: 12%", view.UsageTooltip);
    }

    [Theory]
    [InlineData("five_hour", "5-hour window")]
    [InlineData("seven_day", "Weekly")]
    [InlineData("seven_day_opus", "Weekly (Opus)")]
    [InlineData("spend_limit", "Spend limit")]
    [InlineData("seven_day_cowork", "Seven day cowork")]
    [InlineData("MONTHLY-CREDITS", "Monthly credits")]
    [InlineData("__", "Other window")]
    public void Window_names(string kind, string expected) => Assert.Equal(expected, Format.WindowName(kind));
}

/// <summary>Which sources the settings register, and that "feed" never
/// reaches the usage endpoint or the sign-in.</summary>
public class ClaudeSourceSelectionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class CountingSource(string id, SourceTrust trust, UsageSnapshot? reading) : IClaudeUsageSource, IDisposable
    {
        public string SourceId => id;
        public SourceTrust Trust => trust;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<UsageSnapshot?> TryGetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(reading);
        }

        public void Dispose() => Disposed = true;
    }

    private AppServices Create(string claudeSource, Func<Func<DateTimeOffset>, (IClaudeUsageSource, IReadOnlyList<IDisposable>)> oauth) =>
        AppServices.Create(new AppSettings { ClaudeSource = claudeSource },
            new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false, oauth);

    private static (IClaudeUsageSource, IReadOnlyList<IDisposable>) NeverBuilt(Func<DateTimeOffset> _) =>
        throw new InvalidOperationException("The usage endpoint source must not be built in this mode.");

    private void WriteFeedRecord(double fiveHour, DateTimeOffset at)
    {
        Directory.CreateDirectory(_dir.Combine("feed"));
        File.WriteAllText(_dir.Combine("feed", "claude-s.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            sessionId = "s",
            observedAt = at.ToUnixTimeMilliseconds(),
            rateLimitsAt = at.ToUnixTimeMilliseconds(),
            rateLimits = new[] { new { kind = "five_hour", percentUsed = fiveHour, resetsAt = at.AddHours(2).ToString("O") } }
        }));
    }

    [Fact]
    public async Task Feed_mode_never_builds_or_asks_the_usage_endpoint_source()
    {
        WriteFeedRecord(33, DateTimeOffset.UtcNow);
        using var services = Create("feed", NeverBuilt);

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.Equal([ClaudeFeedUsageSource.Id], services.Claude.Sources.Select(s => s.SourceId));
        Assert.Equal(33, snapshot.FiveHourUtilizationPercent);
        Assert.Equal(ClaudeFeedUsageSource.Id, snapshot.SourceId);
        Assert.Equal(SourceTrust.Official, snapshot.Trust);
    }

    [Fact]
    public async Task Feed_mode_without_a_feed_says_how_to_connect_instead_of_falling_back()
    {
        using var services = Create("feed", NeverBuilt);

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.False(snapshot.HasReading);
        Assert.Equal(ClaudeFeedUsageSource.WaitingDetail, snapshot.StatusDetail);
    }

    [Fact]
    public void Auto_mode_registers_the_feed_ahead_of_the_usage_endpoint()
    {
        var oauth = new CountingSource("claude-oauth", SourceTrust.Unofficial, null);
        using var services = Create("auto", _ => (oauth, [oauth]));

        Assert.Equal([ClaudeFeedUsageSource.Id, "claude-oauth"], services.Claude.Sources.Select(s => s.SourceId));
        Assert.NotNull(services.ClaudeFeed);
    }

    [Fact]
    public async Task Auto_mode_keeps_the_usage_endpoint_quiet_while_the_feed_is_current()
    {
        WriteFeedRecord(33, DateTimeOffset.UtcNow.AddSeconds(-5));
        var oauth = new CountingSource("claude-oauth", SourceTrust.Unofficial, null);
        using var services = Create("auto", _ => (oauth, [oauth]));

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.Equal(33, snapshot.FiveHourUtilizationPercent);
        Assert.Equal(0, oauth.Calls);
    }

    [Fact]
    public async Task Auto_mode_falls_back_to_the_usage_endpoint_without_a_feed()
    {
        var reading = new UsageSnapshot { Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 51, FetchedAt = DateTimeOffset.UtcNow };
        var oauth = new CountingSource("claude-oauth", SourceTrust.Unofficial, reading);
        using var services = Create("auto", _ => (oauth, [oauth]));

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.Equal(51, snapshot.FiveHourUtilizationPercent);
        Assert.Equal("claude-oauth", snapshot.SourceId);
        Assert.Equal(1, oauth.Calls);
    }

    [Fact]
    public async Task Auto_mode_prefers_a_stale_endpoint_reading_with_numbers_over_an_empty_feed()
    {
        var reading = new UsageSnapshot
        {
            Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 51, IsStale = true,
            FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
        };
        var oauth = new CountingSource("claude-oauth", SourceTrust.Unofficial, reading);
        using var services = Create("auto", _ => (oauth, [oauth]));

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.Equal(51, snapshot.FiveHourUtilizationPercent);
    }

    [Fact]
    public async Task Auto_mode_shows_the_endpoints_sign_in_request_over_an_empty_feed()
    {
        var reading = new UsageSnapshot { RequiresReauth = true, IsStale = true, FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var oauth = new CountingSource("claude-oauth", SourceTrust.Unofficial, reading);
        using var services = Create("auto", _ => (oauth, [oauth]));

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.True(snapshot.RequiresReauth);
        Assert.Equal("claude-oauth", snapshot.SourceId);
    }

    [Fact]
    public void Legacy_mode_has_only_the_usage_endpoint()
    {
        var oauth = new CountingSource("claude-oauth", SourceTrust.Unofficial, null);
        using var services = Create("legacy", _ => (oauth, [oauth]));

        Assert.Equal(["claude-oauth"], services.Claude.Sources.Select(s => s.SourceId));
        // The feed folder is still read, for the session lights and exact costs, but never for the limits.
        Assert.NotNull(services.ClaudeFeed);
        Assert.DoesNotContain(services.ClaudeFeed, services.Claude.Sources);
    }

    [Fact]
    public async Task Off_mode_reads_nothing_and_says_so()
    {
        using var services = Create("off", NeverBuilt);

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.Empty(services.Claude.Sources);
        Assert.Equal(AppServices.UsageOffDetail, snapshot.StatusDetail);
    }

    [Fact]
    public void Disposing_the_services_disposes_the_endpoint_source()
    {
        var oauth = new CountingSource("claude-oauth", SourceTrust.Unofficial, null);
        var services = Create("auto", _ => (oauth, [oauth]));

        services.Dispose();

        Assert.True(oauth.Disposed);
    }

    [Theory]
    // Unknown or empty values are the new-install default (the plugin only):
    // a typo never opts anyone into reading their sign-in.
    [InlineData(null, ClaudeSourceMode.Feed)]
    [InlineData("", ClaudeSourceMode.Feed)]
    [InlineData("auto", ClaudeSourceMode.Auto)]
    [InlineData("Feed", ClaudeSourceMode.Feed)]
    [InlineData(" legacy ", ClaudeSourceMode.Legacy)]
    [InlineData("OFF", ClaudeSourceMode.Off)]
    [InlineData("plugin", ClaudeSourceMode.Feed)]
    public void ClaudeSource_values(string? value, ClaudeSourceMode expected) =>
        Assert.Equal(expected, ClaudeSourceModes.Parse(value));

    [Fact]
    public void A_settings_file_without_ClaudeSource_uses_auto_and_keeps_a_chosen_one()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":2,\"Theme\":\"light\"}");
        Assert.Equal("auto", SettingsService.Load(path).ClaudeSource);

        var settings = SettingsService.Load(path);
        settings.ClaudeSource = "legacy";
        SettingsService.Save(settings, path);

        Assert.Equal("legacy", SettingsService.Load(path).ClaudeSource);
    }
}

/// <summary>One refresh at a time, and a feed change during one is not lost.</summary>
public class RefreshGateTests
{
    [Fact]
    public void Only_one_refresh_runs_at_a_time()
    {
        var gate = new RefreshGate();

        Assert.True(gate.TryStart());
        Assert.False(gate.TryStart());
        Assert.False(gate.Finish());
        Assert.True(gate.TryStart());
    }

    [Fact]
    public void A_change_while_idle_refreshes_at_once()
    {
        var gate = new RefreshGate();

        Assert.True(gate.RequestRefresh());
    }

    [Fact]
    public void A_change_during_a_refresh_asks_for_exactly_one_more()
    {
        var gate = new RefreshGate();
        gate.TryStart();

        Assert.False(gate.RequestRefresh());
        Assert.False(gate.RequestRefresh());
        Assert.True(gate.Finish());

        gate.TryStart();
        Assert.False(gate.Finish());
    }
}
