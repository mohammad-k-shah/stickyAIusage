using System.Text.Json.Nodes;
using StickyAIusage.Services;
using StickyAIusage.Stats;
using Xunit;

namespace StickyAIusage.Tests;

/// <summary>The year card's pass over a year of transcripts: the session cards'
/// counting rules, local days, the per-month cache, and the index as the only way
/// to find files.</summary>
public class YearStatsTests
{
    /// <summary>A fixed +05:30 zone, so day boundaries don't depend on the machine.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test +05:30", TimeSpan.FromMinutes(330), "Test", "Test");

    /// <summary>15 December 2026, 10:00 in the test zone.</summary>
    private static readonly DateTimeOffset December15 = new(2026, 12, 15, 10, 0, 0, TimeSpan.FromMinutes(330));

    private static string NewSession(TempDir dir, string project, out string id)
    {
        id = Guid.NewGuid().ToString();
        return dir.Combine("projects", project, id + ".jsonl");
    }

    private static YearStatsService Service(TempDir dir, TranscriptIndex index, Func<DateTimeOffset>? clock = null) =>
        new(index, dir.Combine("data", "cache", "stats"), clock ?? (() => December15), Zone);

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.FromMinutes(330));

    /// <summary>Puts the file's last write where its newest record is, as Claude Code leaves it.</summary>
    private static void Written(string path, DateTimeOffset at) => File.SetLastWriteTimeUtc(path, at.UtcDateTime);

    [Fact]
    public void The_fixture_sessions_add_up_to_what_their_session_cards_say()
    {
        using var dir = new TempDir();
        var fixtures = new (string File, string Id)[]
        {
            ("fable-5-2.jsonl", "5f2a7c10-1b3e-4d55-9a61-0c8e2f4b7a01"),
            ("opus-5-5.jsonl", "0b9d3e44-6a21-4f8c-8d17-5e3a9c2b1f02"),
            ("sonnet-5-5.jsonl", "7c4e1a90-2d5b-4b3f-a8e6-91f0d3c5b203"),
            ("haiku-5-5.jsonl", "e3a8f5d2-9c17-4a06-b4d2-6f1e8a0c7d04")
        };
        var paths = new List<string>();
        foreach (var (file, id) in fixtures)
        {
            var path = dir.Combine("projects", "proj", id + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "transcripts", file), path);
            paths.Add(path);
        }
        var reader = Readers.For(dir);
        var cards = paths.Select(p => reader.ReadSession(p)!).ToList();

        using var index = new TranscriptIndex(dir.Combine("projects"));
        var pass = Service(dir, index).Scan(2026);

        Assert.Equal(cards.Sum(c => c.TotalTokens), pass.Summary.TotalTokens);
        Assert.Equal(cards.Sum(c => c.InputTokens), pass.Summary.InputTokens);
        Assert.Equal(cards.Sum(c => c.OutputTokens), pass.Summary.OutputTokens);
        Assert.Equal(cards.Sum(c => c.CacheReadTokens), pass.Summary.CacheReadTokens);
        Assert.Equal(cards.Sum(c => c.CacheCreationTokens), pass.Summary.CacheWriteTokens);
        Assert.Equal(Math.Round(cards.Sum(c => c.EstimatedCostUsd), 2), pass.Summary.EstimatedCostUsd);
        Assert.Equal(4, pass.Summary.Sessions);
        Assert.Equal(1, pass.Summary.ActiveDays); // all four ran on 17 November
        Assert.Equal(new DateOnly(2026, 11, 17), pass.Summary.BusiestDay);
        Assert.False(pass.Summary.CostIsRough);
        Assert.Equal(4, pass.FilesRead);
    }

    [Fact]
    public void A_response_repeated_on_several_lines_counts_once()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path,
            Transcript.User(id, Local(3, 2, 9)),
            Transcript.Assistant(id, "msg_1", Local(3, 2, 9, 1), input: 10, output: 100),
            Transcript.Assistant(id, "msg_1", Local(3, 2, 9, 1), input: 10, output: 100),
            Transcript.Assistant(id, "msg_1", Local(3, 2, 9, 1), input: 10, output: 100),
            Transcript.Assistant(id, "msg_2", Local(3, 2, 9, 2), input: 5, output: 50));
        Written(path, Local(3, 2, 9, 2));
        using var index = new TranscriptIndex(dir.Combine("projects"));

        var summary = Service(dir, index).Scan(2026).Summary;

        Assert.Equal(165, summary.TotalTokens);
        Assert.Equal(1, summary.Sessions);
    }

    [Fact]
    public void Subagent_turns_and_subagent_files_are_left_out()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path,
            Transcript.Assistant(id, "msg_main", Local(3, 2, 9), output: 100),
            Transcript.Assistant(id, "msg_side", Local(3, 2, 9, 1), output: 5000, sidechain: true));
        Written(path, Local(3, 2, 9, 1));
        var agent = dir.Combine("projects", "p", id, "subagents", "agent-a1b2.jsonl");
        Transcript.Write(agent, Transcript.Assistant(id, "msg_agent", Local(3, 2, 9, 1), output: 7000));
        Written(agent, Local(3, 2, 9, 1));
        using var index = new TranscriptIndex(dir.Combine("projects"));

        var pass = Service(dir, index).Scan(2026);

        Assert.Equal(100, pass.Summary.TotalTokens);
        Assert.Equal(1, pass.FilesRead);
    }

    [Fact]
    public void A_fork_counts_only_its_own_records_and_a_file_naming_no_session_counts_all()
    {
        using var dir = new TempDir();
        var parent = Guid.NewGuid().ToString();
        var fork = NewSession(dir, "p", out var forkId);
        Transcript.Write(fork,
            Transcript.Assistant(parent, "msg_parent_1", Local(2, 1, 9), output: 9000),
            Transcript.Assistant(parent, "msg_parent_2", Local(2, 1, 10), output: 9000),
            Transcript.User(forkId, Local(3, 5, 9)),
            Transcript.Assistant(forkId, "msg_fork", Local(3, 5, 9, 1), output: 300));
        Written(fork, Local(3, 5, 9, 1));
        // An unexpected shape: nothing in the file names the file's session.
        var odd = NewSession(dir, "q", out _);
        var other = Guid.NewGuid().ToString();
        Transcript.Write(odd, Transcript.Assistant(other, "msg_odd", Local(3, 6, 9), output: 40));
        Written(odd, Local(3, 6, 9));
        using var index = new TranscriptIndex(dir.Combine("projects"));

        var summary = Service(dir, index).Scan(2026).Summary;

        Assert.Equal(340, summary.TotalTokens);
        Assert.Equal(2, summary.ActiveDays); // 5 and 6 March; the parent's 1 February is not this session's
        Assert.Equal(2, summary.Sessions);
    }

    [Fact]
    public void A_fork_without_an_answer_of_its_own_yet_counts_nothing_of_its_parent_s()
    {
        using var dir = new TempDir();
        var parent = Guid.NewGuid().ToString();
        var fork = NewSession(dir, "p", out var forkId);
        Transcript.Write(fork,
            Transcript.Assistant(parent, "msg_parent", Local(3, 5, 8), output: 9000),
            Transcript.User(forkId, Local(3, 5, 9)));
        Written(fork, Local(3, 5, 9));
        using var index = new TranscriptIndex(dir.Combine("projects"));
        var card = Readers.For(dir).ReadSession(fork)!;

        var summary = Service(dir, index).Scan(2026).Summary;

        Assert.Equal(0, card.TotalTokens); // as its session card says
        Assert.Equal(0, summary.TotalTokens);
    }

    [Fact]
    public void Days_are_local_days_and_other_years_are_left_out()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path,
            // 31 December 2025, 23:00 here: last year.
            Transcript.Assistant(id, "msg_old", Local(1, 1, 0).AddHours(-1), output: 1000),
            // 1 January 00:15 here = 31 December 18:45 UTC: this year, on 1 January.
            Transcript.Assistant(id, "msg_new_year", Local(1, 1, 0, 15), output: 10),
            // 2 March 23:50 here = 18:20 UTC.
            Transcript.Assistant(id, "msg_late", Local(3, 2, 23, 50), output: 20));
        Written(path, Local(3, 2, 23, 50));
        using var index = new TranscriptIndex(dir.Combine("projects"));

        var service = Service(dir, index);
        service.Scan(2026);
        var days = new DailyStatsStore(dir.Combine("data", "cache", "stats"));

        var january = days.Load(2026, 1, Zone.Id, out _);
        var march = days.Load(2026, 3, Zone.Id, out _);
        Assert.Equal(10, january[new DateOnly(2026, 1, 1)].Tokens);
        Assert.Equal(1, january[new DateOnly(2026, 1, 1)].Hours[0]);
        Assert.Equal(20, march[new DateOnly(2026, 3, 2)].Tokens);
        Assert.Equal(1, march[new DateOnly(2026, 3, 2)].Hours[23]);
        Assert.Equal(30, service.Scan(2026).Summary.TotalTokens);
    }

    [Fact]
    public void A_second_pass_reads_only_what_was_written_since_the_first_day_it_lacks()
    {
        using var dir = new TempDir();
        var march = NewSession(dir, "p", out var a);
        Transcript.Write(march, Transcript.Assistant(a, "m1", Local(3, 2, 9), output: 100, model: "claude-opus-5-5"));
        Written(march, Local(3, 2, 9));
        var today = NewSession(dir, "p", out var b);
        Transcript.Write(today, Transcript.Assistant(b, "m2", Local(12, 15, 9), output: 7, model: "claude-sonnet-5-5"));
        Written(today, Local(12, 15, 9));
        using var index = new TranscriptIndex(dir.Combine("projects"));

        var first = Service(dir, index).Scan(2026);
        // A new service (a restart): the finished days come from the cache.
        var second = Service(dir, index).Scan(2026);

        Assert.Equal(2, first.FilesRead);
        Assert.Equal(0, first.DaysFromCache);
        Assert.Equal(348, first.DaysSaved); // 1 January – 14 December; today isn't over
        Assert.Equal(1, second.FilesRead); // only today's transcript
        Assert.Equal(348, second.DaysFromCache);
        Assert.Equal(0, second.DaysSaved);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(107, second.Summary.TotalTokens);
        Assert.True(File.Exists(dir.Combine("data", "cache", "stats", "2026-03.json")));
        var december = new DailyStatsStore(dir.Combine("data", "cache", "stats")).Load(2026, 12, Zone.Id, out _);
        Assert.True(december.ContainsKey(new DateOnly(2026, 12, 14)));
        Assert.False(december.ContainsKey(new DateOnly(2026, 12, 15)));
    }

    [Fact]
    public void The_cache_answers_for_a_finished_day_without_the_transcripts()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path, Transcript.Assistant(id, "m1", Local(3, 2, 9), output: 100));
        Written(path, Local(3, 2, 9));
        using (var index = new TranscriptIndex(dir.Combine("projects")))
            Service(dir, index).Scan(2026);

        File.Delete(path);
        using var after = new TranscriptIndex(dir.Combine("projects"));
        var pass = Service(dir, after).Scan(2026);

        Assert.Equal(100, pass.Summary.TotalTokens);
        Assert.Equal(0, pass.FilesRead);
    }

    [Fact]
    public void A_day_is_kept_only_once_it_has_been_over_for_an_hour()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path, Transcript.Assistant(id, "m1", Local(12, 14, 23, 59), output: 100));
        Written(path, Local(12, 14, 23, 59));
        using var index = new TranscriptIndex(dir.Combine("projects"));
        var store = new DailyStatsStore(dir.Combine("data", "cache", "stats"));

        var early = Service(dir, index, () => Local(12, 15, 0, 30)).Scan(2026);
        Assert.False(store.Load(2026, 12, Zone.Id, out _).ContainsKey(new DateOnly(2026, 12, 14)));
        Assert.True(store.Load(2026, 12, Zone.Id, out _).ContainsKey(new DateOnly(2026, 12, 13)));
        Assert.Equal(100, early.Summary.TotalTokens);

        Service(dir, index, () => Local(12, 15, 1, 0)).Scan(2026);
        Assert.Equal(100, store.Load(2026, 12, Zone.Id, out _)[new DateOnly(2026, 12, 14)].Tokens);
    }

    [Fact]
    public void A_month_file_from_another_zone_or_that_does_not_parse_is_counted_again()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path, Transcript.Assistant(id, "m1", Local(3, 2, 9), output: 100));
        Written(path, Local(3, 2, 9));
        using var index = new TranscriptIndex(dir.Combine("projects"));
        var file = dir.Combine("data", "cache", "stats", "2026-03.json");
        Service(dir, index).Scan(2026);

        // A cached day is trusted as it is: change it and the pass reports the change...
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["days"]!["2026-03-02"]!["models"]!["claude-sonnet-5"]!["output"] = 5;
        File.WriteAllText(file, json.ToJsonString());
        Assert.Equal(5, Service(dir, index).Scan(2026).Summary.TotalTokens);

        // ...but not when the file was written in another zone, or is broken.
        json["zone"] = "Elsewhere";
        File.WriteAllText(file, json.ToJsonString());
        Assert.Equal(100, Service(dir, index).Scan(2026).Summary.TotalTokens);
        File.WriteAllText(file, "{ not json");
        var again = Service(dir, index).Scan(2026);
        Assert.Equal(100, again.Summary.TotalTokens);
        Assert.Equal(1, again.FilesRead);
        Assert.Contains("\"schema\":1", File.ReadAllText(file)); // rewritten whole
    }

    [Fact]
    public void Days_priced_with_another_price_list_are_counted_again()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path, Transcript.Assistant(id, "m1", Local(3, 2, 9), output: 100));
        Written(path, Local(3, 2, 9));
        using var index = new TranscriptIndex(dir.Combine("projects"));
        var file = dir.Combine("data", "cache", "stats", "2026-03.json");
        Service(dir, index).Scan(2026);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(YearStatsService.PricesId(ModelCatalog.Current), (string?)json["prices"]);

        json["days"]!["2026-03-02"]!["models"]!["claude-sonnet-5"]!["output"] = 5;
        json["prices"] = "2026-01-01 built-in";
        File.WriteAllText(file, json.ToJsonString());
        var again = Service(dir, index).Scan(2026);

        Assert.Equal(100, again.Summary.TotalTokens);
        Assert.Equal(1, again.FilesRead);
    }

    [Fact]
    public void A_newer_version_s_month_file_is_neither_used_nor_overwritten()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path, Transcript.Assistant(id, "m1", Local(3, 2, 9), output: 100));
        Written(path, Local(3, 2, 9));
        var file = dir.Combine("data", "cache", "stats", "2026-03.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const string newer = "{\"schema\":2,\"month\":\"2026-03\",\"zone\":\"Test +05:30\",\"days\":{}}";
        File.WriteAllText(file, newer);
        using var index = new TranscriptIndex(dir.Combine("projects"));

        var pass = Service(dir, index).Scan(2026);

        Assert.Equal(100, pass.Summary.TotalTokens);
        Assert.Equal(newer, File.ReadAllText(file));
    }

    [Fact]
    public void The_pass_asks_the_index_and_never_walks_the_folder_itself()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 5; i++)
        {
            var path = NewSession(dir, "p" + i, out var id);
            Transcript.Write(path, Transcript.Assistant(id, "m" + i, Local(4, 1 + i, 9), output: 10));
            Written(path, Local(4, 1 + i, 9));
        }
        using var index = new TranscriptIndex(dir.Combine("projects"));
        Assert.True(index.WaitUntilReady(TimeSpan.FromSeconds(10)));

        var service = Service(dir, index);
        service.Scan(2026);
        service.Scan(2026);

        Assert.Equal(1, index.WalkCount);
    }

    [Fact]
    public void The_favorite_model_has_the_most_cost_and_the_busiest_day_the_most_tokens()
    {
        var days = new Dictionary<DateOnly, DayStats>
        {
            // Opus: $7, 86,000 tokens. Sonnet: $2, 182,000 tokens (more tokens, less cost).
            [new DateOnly(2026, 5, 1)] = Day(("claude-opus-5-5", 1000, 5m), ("claude-haiku-5-5", 80_000, 0.5m)),
            [new DateOnly(2026, 5, 2)] = Day(("claude-opus-5-5", 85_000, 2m)),
            [new DateOnly(2026, 5, 3)] = Day(("claude-sonnet-5-5", 91_000, 1m)),
            [new DateOnly(2026, 5, 4)] = new DayStats(), // a quiet day isn't active
            // Responses that named no model: the most cost of all, but never a favorite.
            [new DateOnly(2026, 5, 5)] = Day((DayStats.UnknownModel, 10, 50m)),
            [new DateOnly(2026, 5, 6)] = Day(("claude-sonnet-5-5", 91_000, 1m)) // as many tokens as 3 May: the earlier day wins
        };
        days[new DateOnly(2026, 5, 1)].Sessions.UnionWith(["a", "b"]);
        days[new DateOnly(2026, 5, 2)].Sessions.UnionWith(["b"]);
        days[new DateOnly(2026, 5, 3)].Sessions.UnionWith(["c"]);
        days[new DateOnly(2026, 5, 5)].Sessions.UnionWith(["d"]);
        days[new DateOnly(2026, 5, 6)].Sessions.UnionWith(["c"]);

        var summary = YearSummary.Sum(2026, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), days);

        Assert.Equal("Opus 5.5", summary.FavoriteModel);
        Assert.Equal(new DateOnly(2026, 5, 3), summary.BusiestDay);
        Assert.Equal(91_000, summary.BusiestDayTokens);
        Assert.Equal(5, summary.ActiveDays);
        Assert.Equal(4, summary.Sessions); // b and c ran on two days and count once
        Assert.Equal(59.5m, summary.EstimatedCostUsd);
        Assert.Equal(348_010, summary.TotalTokens); // 81,000 + 85,000 + 91,000 + 10 + 91,000
        // Days outside the range don't count.
        Assert.Equal(91_010, YearSummary.Sum(2026, new DateOnly(2026, 5, 5), new DateOnly(2026, 5, 6), days).TotalTokens);

        static DayStats Day(params (string Model, long Output, decimal Cost)[] models)
        {
            var day = new DayStats();
            foreach (var (model, output, cost) in models)
                day.Models[model] = new ModelDayStats { Output = output, CostUsd = cost, Responses = 1 };
            return day;
        }
    }

    [Fact]
    public void A_model_the_price_list_does_not_know_marks_the_cost_rough()
    {
        using var dir = new TempDir();
        var path = NewSession(dir, "p", out var id);
        Transcript.Write(path,
            Transcript.Assistant(id, "m1", Local(6, 1, 9), output: 1_000_000, model: "claude-nova-1"),
            Transcript.Assistant(id, "m2", Local(6, 1, 9, 5), output: 10, model: "claude-haiku-5-5"));
        Written(path, Local(6, 1, 9, 5));
        using var index = new TranscriptIndex(dir.Combine("projects"));

        var summary = Service(dir, index).Scan(2026).Summary;

        Assert.True(summary.CostIsRough);
        Assert.Equal(1_000_010, summary.TotalTokens);
        Assert.NotNull(summary.FavoriteModel);
    }

    [Fact]
    public void A_transcript_that_can_t_be_read_is_skipped_and_the_rest_still_count()
    {
        using var dir = new TempDir();
        var readable = NewSession(dir, "p", out var a);
        Transcript.Write(readable, Transcript.Assistant(a, "m1", Local(8, 1, 9), output: 100));
        Written(readable, Local(8, 1, 9));
        var locked = NewSession(dir, "q", out var b);
        Transcript.Write(locked, Transcript.Assistant(b, "m2", Local(8, 2, 9), output: 7));
        Written(locked, Local(8, 2, 9));
        using var index = new TranscriptIndex(dir.Combine("projects"));
        Assert.True(index.WaitUntilReady(TimeSpan.FromSeconds(10)));

        YearPass pass;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            pass = Service(dir, index).Scan(2026);

        // .NET keeps FileShare.None on every OS (Windows' sharing modes; flock on macOS and Linux,
        // seen on the macOS runner): the locked file is skipped everywhere.
        Assert.Equal(100, pass.Summary.TotalTokens);
        Assert.Equal(2, pass.FilesRead);
    }

    [Fact]
    public void A_future_year_or_an_empty_folder_gives_an_empty_summary()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("projects"));
        using var index = new TranscriptIndex(dir.Combine("projects"));
        var service = Service(dir, index);

        Assert.Equal(0, service.Scan(2027).Summary.TotalTokens);
        var empty = service.Scan(2026).Summary;
        Assert.Equal(0, empty.TotalTokens);
        Assert.Null(empty.FavoriteModel);
        Assert.Null(empty.BusiestDay);
    }

    [Fact]
    public async Task Callers_during_a_pass_share_it_and_a_fresh_result_is_reused()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 3; i++)
        {
            var path = NewSession(dir, "p", out var id);
            Transcript.Write(path, Transcript.Assistant(id, "m" + i, Local(7, 1 + i, 9), output: 10));
            Written(path, Local(7, 1 + i, 9));
        }
        using var index = new TranscriptIndex(dir.Combine("projects"));
        var now = December15;
        var service = Service(dir, index, () => now);
        var seen = new List<StatsProgress>();
        var progress = new SyncProgress(p => { lock (seen) seen.Add(p); });

        var first = service.GetAsync(2026, progress);
        var second = service.GetAsync(2026);
        var results = await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Equal(30, results[0].TotalTokens);
        Assert.Contains(new StatsProgress(3, 3), seen);
        Assert.Equal(new StatsProgress(0, 3), seen[0]);
        var reused = service.LastPass;
        Assert.Equal(30, (await service.GetAsync(2026)).TotalTokens);
        Assert.Same(reused, service.LastPass); // within a minute: no new pass

        now = now.AddMinutes(2);
        await service.GetAsync(2026);
        Assert.NotSame(reused, service.LastPass);
    }

    [Fact]
    public void A_midnight_skipped_by_a_clock_change_starts_the_day_at_the_next_valid_time()
    {
        // Clocks go forward at midnight on the first Sunday of September (as in some zones).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 9, 1, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 1, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Midnight DST", TimeSpan.FromHours(-4), "Midnight DST", "Standard", "Daylight", [rule]);
        var day = new DateOnly(2026, 9, 6); // the first Sunday of September 2026

        var start = YearStatsService.LocalMidnightUtc(day, zone);

        Assert.True(zone.IsInvalidTime(day.ToDateTime(TimeOnly.MinValue)));
        Assert.Equal(new DateTime(2026, 9, 6, 4, 0, 0, DateTimeKind.Utc), start); // 01:00 daylight = 04:00 UTC
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the pool).</summary>
    private sealed class SyncProgress(Action<StatsProgress> report) : IProgress<StatsProgress>
    {
        public void Report(StatsProgress value) => report(value);
    }
}
