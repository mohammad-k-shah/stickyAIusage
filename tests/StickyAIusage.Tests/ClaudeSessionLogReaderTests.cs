using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class ClaudeSessionLogReaderTests : IDisposable
{
    private readonly TempDir _root = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public void Dispose() => _root.Dispose();

    private string SessionFile(string sessionId) => _root.Combine("C--work-acme", sessionId + ".jsonl");

    [Fact]
    public void Message_repeated_once_per_content_block_is_counted_once()
    {
        var id = Guid.NewGuid().ToString();
        var lines = new List<string> { Transcript.User(id, Now.AddMinutes(-2)) };
        // Live transcripts showed one message.id repeated 16 times with identical usage.
        for (var i = 0; i < 16; i++)
            lines.Add(Transcript.Assistant(id, "msg_A", Now.AddMinutes(-1), input: 2, output: 2938, cacheRead: 77236, cacheWrite: 5861));
        lines.Add(Transcript.Assistant(id, "msg_B", Now, input: 2, output: 100, cacheRead: 1000, cacheWrite: 10));
        Transcript.Write(SessionFile(id), lines.ToArray());

        var s = ClaudeSessionLogReader.ReadSession(SessionFile(id))!;

        Assert.Equal(4, s.InputTokens);
        Assert.Equal(3038, s.OutputTokens);
        Assert.Equal(78236, s.CacheReadTokens);
        Assert.Equal(5871, s.CacheCreationTokens);
    }

    [Fact]
    public void Fork_counts_only_its_own_records_for_tokens_start_time_model_and_compactions()
    {
        var parent = Guid.NewGuid().ToString();
        var fork = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(fork),
            // Copied in from the parent conversation, which started 6 hours ago.
            Transcript.User(parent, Now.AddHours(-6), effort: "max"),
            Transcript.Assistant(parent, "msg_P1", Now.AddHours(-6), input: 10, output: 20000, cacheRead: 5000, model: "claude-opus-5"),
            Transcript.CompactBoundary(parent, Now.AddHours(-5)),
            Transcript.Assistant(parent, "msg_P2", Now.AddHours(-5), input: 10, output: 15000, cacheRead: 6000, model: "claude-opus-5"),
            // The fork's own activity, 7 minutes long.
            Transcript.User(fork, Now.AddMinutes(-7), effort: "medium"),
            Transcript.Assistant(fork, "msg_F1", Now.AddMinutes(-6), input: 20, output: 1500, cacheRead: 1_500_000, cacheWrite: 200_000),
            Transcript.Assistant(fork, "msg_F2", Now, input: 30, output: 2500, cacheRead: 1_800_000, cacheWrite: 270_000));

        var s = ClaudeSessionLogReader.ReadSession(SessionFile(fork))!;

        Assert.Equal(fork, s.SessionId);
        Assert.Equal(50, s.InputTokens);
        Assert.Equal(4000, s.OutputTokens);
        Assert.Equal(3_300_000, s.CacheReadTokens);
        Assert.Equal(470_000, s.CacheCreationTokens);
        Assert.Equal("claude-sonnet-5", s.Model);
        Assert.Equal("medium", s.ThinkingEffort);
        Assert.Equal(0, s.CompactionCount);
        Assert.InRange(s.Elapsed, TimeSpan.FromMinutes(6.9), TimeSpan.FromMinutes(7.1));
    }

    [Fact]
    public void File_whose_records_never_match_its_name_falls_back_to_counting_everything()
    {
        var fileId = Guid.NewGuid().ToString();
        var otherId = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(fileId),
            Transcript.User(otherId, Now.AddMinutes(-1)),
            Transcript.Assistant(otherId, "msg_1", Now, input: 5, output: 50));

        var s = ClaudeSessionLogReader.ReadSession(SessionFile(fileId))!;

        Assert.Equal(5, s.InputTokens);
        Assert.Equal(50, s.OutputTokens);
    }

    [Fact]
    public void Sidechain_records_are_not_counted()
    {
        var id = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(id),
            Transcript.User(id, Now.AddMinutes(-1)),
            Transcript.Assistant(id, "msg_1", Now, output: 100),
            Transcript.Assistant(id, "msg_2", Now, output: 9999, sidechain: true));

        Assert.Equal(100, ClaudeSessionLogReader.ReadSession(SessionFile(id))!.OutputTokens);
    }

    [Fact]
    public void Subagent_and_non_guid_transcripts_are_not_listed_as_sessions()
    {
        var id = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(id),
            Transcript.User(id, Now.AddMinutes(-2)),
            // Enough output to cost more than $0.00 - sessions that cost
            // nothing aren't listed (see the test below).
            Transcript.Assistant(id, "msg_1", Now.AddMinutes(-2), output: 100_000));

        // Written *after* the main session, as happens while a subagent runs.
        var subagentFile = _root.Combine("C--work-acme", id, "subagents", "agent-a5847e166f1b98c15.jsonl");
        Transcript.Write(subagentFile,
            Transcript.Assistant(id, "msg_S", Now, output: 5, sidechain: true));
        File.SetLastWriteTime(subagentFile, DateTime.Now.AddSeconds(5));
        Transcript.Write(_root.Combine("C--work-acme", "notes.jsonl"), Transcript.User(id, Now));

        var today = ClaudeSessionLogReader.GetSessionsForDate(DateOnly.FromDateTime(DateTime.Now), _root.Path);
        var current = ClaudeSessionLogReader.GetMostRecentSession(_root.Path);
        var activity = ClaudeSessionLogReader.GetMostRecentActivityUtc(_root.Path);

        Assert.Equal(id, Assert.Single(today).SessionId);
        Assert.Equal(id, current!.SessionId);
        // Still counts as "Claude is active" for adaptive polling.
        Assert.Equal(File.GetLastWriteTimeUtc(subagentFile), activity);
    }

    [Fact]
    public void A_session_opened_without_sending_anything_is_not_listed()
    {
        // Claude Code writes a transcript as soon as it opens in a folder.
        // Until a message is sent it has no tokens and costs $0.00, and the
        // owner doesn't want those on the panel.
        var empty = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(empty), Transcript.User(empty, Now));

        var worked = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(worked),
            Transcript.User(worked, Now.AddMinutes(-1)),
            Transcript.Assistant(worked, "msg_1", Now, output: 100_000));

        var today = ClaudeSessionLogReader.GetSessionsForDate(DateOnly.FromDateTime(DateTime.Now), _root.Path);

        Assert.Equal(worked, Assert.Single(today).SessionId);
        Assert.Equal(2, ClaudeSessionLogReader.GetSessionsForDate(
            DateOnly.FromDateTime(DateTime.Now), _root.Path, onlyWithWork: false).Count);
    }

    [Fact]
    public void A_date_range_lists_sessions_last_active_on_any_day_in_it()
    {
        var old = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(old),
            Transcript.Assistant(old, "msg_1", Now.AddDays(-3), output: 100_000));
        File.SetLastWriteTime(SessionFile(old), DateTime.Now.AddDays(-3));

        var fresh = Guid.NewGuid().ToString();
        Transcript.Write(SessionFile(fresh),
            Transcript.Assistant(fresh, "msg_1", Now, output: 100_000));

        var today = DateOnly.FromDateTime(DateTime.Now);
        List<string> Ids(int days) => ClaudeSessionLogReader
            .GetSessionsInRange(today.AddDays(1 - days), today, _root.Path).Select(s => s.SessionId).ToList();

        Assert.Equal([fresh], Ids(1));
        Assert.Equal([fresh], Ids(2));
        Assert.Equal([fresh, old], Ids(7)); // most recently active first
    }

    [Fact]
    public void Incremental_reads_pick_up_appended_lines_and_wait_for_partial_ones()
    {
        var id = Guid.NewGuid().ToString();
        var path = SessionFile(id);
        Transcript.Write(path,
            Transcript.User(id, Now.AddMinutes(-3)),
            Transcript.Assistant(id, "msg_1", Now.AddMinutes(-2), output: 100));
        Assert.Equal(100, ClaudeSessionLogReader.ReadSession(path)!.OutputTokens);

        // Claude Code mid-write: a line without its trailing newline yet.
        var next = Transcript.Assistant(id, "msg_2", Now.AddMinutes(-1), output: 50);
        Transcript.Append(path, next[..20]);
        Assert.Equal(100, ClaudeSessionLogReader.ReadSession(path)!.OutputTokens);

        Transcript.Append(path, next[20..] + "\n" + Transcript.Assistant(id, "msg_2", Now, output: 50) + "\n");
        var incremental = ClaudeSessionLogReader.ReadSession(path)!;
        Assert.Equal(150, incremental.OutputTokens);

        // Same content parsed from scratch (a copy is a new cache entry).
        var copy = _root.Combine("copy", id + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(path, copy);
        var fresh = ClaudeSessionLogReader.ReadSession(copy)!;
        Assert.Equal(fresh.OutputTokens, incremental.OutputTokens);
        Assert.Equal(fresh.LastActivityAt, incremental.LastActivityAt);
    }

    [Fact]
    public void Truncated_file_is_reparsed_from_the_start()
    {
        var id = Guid.NewGuid().ToString();
        var path = SessionFile(id);
        Transcript.Write(path,
            Transcript.User(id, Now.AddMinutes(-3)),
            Transcript.Assistant(id, "msg_1", Now.AddMinutes(-2), output: 100),
            Transcript.Assistant(id, "msg_2", Now.AddMinutes(-1), output: 200));
        Assert.Equal(300, ClaudeSessionLogReader.ReadSession(path)!.OutputTokens);

        File.WriteAllText(path, Transcript.User(id, Now) + "\n" + Transcript.Assistant(id, "msg_9", Now, output: 7) + "\n");

        Assert.Equal(7, ClaudeSessionLogReader.ReadSession(path)!.OutputTokens);
    }
}

public class EffortTests
{
    [Fact]
    public void Effort_is_read_from_the_current_string_form_and_per_turn_overrides()
    {
        using var dir = new TempDir();
        var id = Guid.NewGuid().ToString();
        var path = dir.Combine("proj", id + ".jsonl");
        var now = DateTimeOffset.UtcNow;
        string Line(string effort, string? perTurn) => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant", sessionId = id, cwd = "/work/acme-webapp", timestamp = now.ToString("O"),
            effort, perTurnEffort = perTurn,
            message = new { id = Guid.NewGuid().ToString(), model = "claude-opus-5", usage = new { input_tokens = 1, output_tokens = 1 } }
        });

        Transcript.Write(path, Line("medium", null), Line("high", null));
        Assert.Equal("high", ClaudeSessionLogReader.ReadSession(path)!.ThinkingEffort);

        Transcript.Append(path, Line("high", "max") + "\n");
        Assert.Equal("max", ClaudeSessionLogReader.ReadSession(path)!.ThinkingEffort);
    }
}
