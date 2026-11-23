using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class TranscriptIndexTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(15);
    private readonly TempDir _dir = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public void Dispose() => _dir.Dispose();

    private string Session(string project, string? id = null) =>
        _dir.Combine(project, (id ?? Guid.NewGuid().ToString()) + ".jsonl");

    private static void WriteWorkedSession(string path, DateTimeOffset at)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        Transcript.Write(path, Transcript.User(id, at.AddMinutes(-1)), Transcript.Assistant(id, "msg_" + id, at, output: 100_000));
    }

    private TranscriptIndex NewIndex(ITranscriptFileSystem? files = null, string? root = null, bool watch = true,
        List<string>? log = null) =>
        new(root ?? _dir.Path, files ?? DiskTranscriptFileSystem.Instance, () => _now, watch, runTimer: false,
            log is null ? null : line => { lock (log) log.Add(line); });

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void After_warm_up_a_hundred_ticks_enumerate_nothing()
    {
        for (var i = 0; i < 3; i++)
            WriteWorkedSession(Session($"C--work-p{i}"), DateTimeOffset.UtcNow.AddMinutes(-i));
        var old = Session("C--work-old");
        WriteWorkedSession(old, DateTimeOffset.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-20));
        Transcript.Write(_dir.Combine("C--work-p0", "subagents", "agent-a1.jsonl"), "{}");
        var files = new CountingFileSystem(DiskTranscriptFileSystem.Instance);
        using var sessions = new ClaudeSessionLogSource(NewIndex(files));
        var today = DateOnly.FromDateTime(DateTime.Now);

        // Warm-up: the first walk.
        Assert.Equal(3, sessions.GetSessionsForDate(today).Count);
        Assert.Equal(1, sessions.Index.WalkCount);

        // macOS's FSEvents can still report the folders written just before the watcher
        // started (seen on the runner: one such event walked its folder again, now and then).
        // Those late events aren't ticks: wait until the watcher is quiet, then count.
        var (walksBefore, fullWalksBefore) = Quiet(files, sessions.Index);

        for (var tick = 0; tick < 100; tick++)
        {
            Assert.NotNull(sessions.GetMostRecentActivityUtc());
            Assert.NotNull(sessions.GetMostRecentSession());
            Assert.Equal(3, sessions.GetSessionsForDate(today).Count);
            Assert.Equal(4, sessions.GetSessionsInRange(today.AddDays(-29), today).Count);
            sessions.Index.Tick();
            _now += TimeSpan.FromSeconds(5); // 100 ticks: under ten minutes
        }

        Assert.Equal(walksBefore, files.Walks);
        Assert.Equal(fullWalksBefore, sessions.Index.WalkCount);
        Assert.Equal(TranscriptIndexMode.Watching, sessions.Index.Mode);

        // The safety walk, ten minutes after the last one.
        _now += TimeSpan.FromMinutes(2);
        sessions.Index.Tick();
        WaitFor(() => sessions.Index.WalkCount == fullWalksBefore + 1, "the safety walk");
        Assert.Equal(walksBefore + 1, files.Walks);
    }

    /// <summary>The walk counts once nothing has changed them for a second (at most ten seconds).</summary>
    private static (int Walks, int FullWalks) Quiet(CountingFileSystem files, TranscriptIndex index)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var counts = (files.Walks, index.WalkCount);
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow - quietSince < TimeSpan.FromSeconds(1) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            var now = (files.Walks, index.WalkCount);
            if (now != counts)
                (counts, quietSince) = (now, DateTime.UtcNow);
        }
        return counts;
    }

    [Fact]
    public void A_question_before_the_first_walk_waits_for_it()
    {
        var files = new MemoryFileSystem(_dir.Path) { WalkDelay = TimeSpan.FromMilliseconds(300) };
        files.Add(Path.Combine(_dir.Path, "p", "a.jsonl"), 10, DateTime.UtcNow);
        using var index = NewIndex(files);

        Assert.Equal(1, index.Count);
        Assert.Equal(1, index.WalkCount);
    }

    [Fact]
    public void The_index_follows_create_append_delete_and_rename()
    {
        var first = Session("C--work-acme");
        WriteWorkedSession(first, DateTimeOffset.UtcNow);
        using var index = NewIndex();
        Assert.Equal(1, index.Count);
        Assert.Equal(TranscriptIndexMode.Watching, index.Mode);

        // Created
        var second = Session("C--work-acme");
        WriteWorkedSession(second, DateTimeOffset.UtcNow);
        WaitFor(() => index.Count == 2, "the new transcript");

        // Appended
        Transcript.Append(second, new string('x', 5000) + "\n");
        var length = new FileInfo(second).Length;
        WaitFor(() => index.Files().Any(f => f.Path == second && f.Length == length), "the appended length");

        // Deleted
        File.Delete(first);
        WaitFor(() => index.Count == 1 && index.Files().All(f => f.Path != first), "the deleted transcript to go");

        // Renamed in (a temp file renamed to its final name) and renamed away
        var temp = _dir.Combine("C--work-acme", "draft.tmp");
        File.WriteAllText(temp, "{}\n");
        var final = Session("C--work-acme");
        File.Move(temp, final);
        WaitFor(() => index.Files().Any(f => f.Path == final), "the renamed-in transcript");
        var away = _dir.Combine("C--work-acme", "kept.txt");
        File.Move(final, away);
        WaitFor(() => index.Files().All(f => f.Path != final), "the renamed-away transcript to go");

        Assert.Equal(1, index.WalkCount); // all of it from the watcher
        Assert.Equal([second], index.Files().Select(f => f.Path));
    }

    [Fact]
    public void A_moved_project_folder_moves_its_transcripts()
    {
        var inside = Session("C--work-acme");
        WriteWorkedSession(inside, DateTimeOffset.UtcNow);
        Transcript.Write(_dir.Combine("C--work-acme", "subagents", "agent-b2.jsonl"), "{}");
        using var index = NewIndex();
        Assert.Equal(2, index.Count);

        Directory.Move(_dir.Combine("C--work-acme"), _dir.Combine("C--work-renamed"));

        var moved = _dir.Combine("C--work-renamed", Path.GetFileName(inside));
        WaitFor(() => index.Files().Select(f => f.Path).Order().SequenceEqual(
            new[] { moved, _dir.Combine("C--work-renamed", "subagents", "agent-b2.jsonl") }.Order()), "the moved folder's transcripts");

        Directory.Delete(_dir.Combine("C--work-renamed"), recursive: true);
        WaitFor(() => index.Count == 0, "the deleted folder's transcripts to go");
        Assert.Null(index.MostRecentWriteUtc);
    }

    [Fact]
    public void A_buffer_overflow_walks_again_and_finds_what_was_missed()
    {
        var files = new MemoryFileSystem(_dir.Path);
        var log = new List<string>();
        using var index = NewIndex(files, log: log);
        Assert.Equal(0, index.Count);
        Assert.Equal(TranscriptIndexMode.Watching, index.Mode);

        // Written while the watcher's buffer overflowed: no event arrives for it.
        var missed = Path.Combine(_dir.Path, "p", Guid.NewGuid() + ".jsonl");
        files.Add(missed, 42, DateTime.UtcNow);
        Assert.Equal(0, index.Count);

        index.OnWatcherError(new InternalBufferOverflowException());

        WaitFor(() => index.Count == 1, "the walk after the overflow");
        Assert.Equal(2, index.WalkCount);
        Assert.Equal(TranscriptIndexMode.Watching, index.Mode);
        Assert.Contains(log, line => line.Contains("buffer overflow"));
    }

    [Fact]
    public void A_stopped_watcher_is_started_again_by_the_walk_it_asks_for()
    {
        using var index = NewIndex();
        Assert.Equal(0, index.Count);

        index.OnWatcherError(new IOException("The network name is no longer available."));
        WaitFor(() => index.WalkCount == 2 && index.Mode == TranscriptIndexMode.Watching, "the walk and a new watcher");

        var later = Session("C--work-acme");
        WriteWorkedSession(later, DateTimeOffset.UtcNow);
        WaitFor(() => index.Count == 1, "an event from the new watcher");
    }

    [Fact]
    public void A_folder_that_cannot_be_watched_is_walked_every_minute()
    {
        // The file system says the folder exists, but there is nothing on disk to watch.
        var root = _dir.Combine("not-on-disk", "projects");
        var files = new MemoryFileSystem(root);
        var log = new List<string>();
        using var index = NewIndex(files, root, log: log);
        Assert.Equal(0, index.Count);
        Assert.Equal(TranscriptIndexMode.Polling, index.Mode);
        Assert.Contains(log, line => line.Contains("walking it every minute"));

        files.Add(Path.Combine(root, "p", Guid.NewGuid() + ".jsonl"), 1, DateTime.UtcNow);
        index.Tick();
        WaitFor(() => index.Count == 1, "the minute's walk");
        index.Tick();
        WaitFor(() => index.WalkCount == 3, "the next minute's walk");
        Assert.Single(log, line => line.Contains("can't watch")); // said once, not every minute
    }

    [Fact]
    public void A_projects_folder_created_later_is_picked_up_at_once()
    {
        var root = _dir.Combine("projects");
        using var index = NewIndex(root: root);
        Assert.Equal(0, index.Count);
        Assert.Equal(TranscriptIndexMode.WaitingForFolder, index.Mode);

        Directory.CreateDirectory(root);
        WaitFor(() => index.Mode == TranscriptIndexMode.Watching, "the parent folder's watcher");
        WriteWorkedSession(Path.Combine(root, "C--work-acme", Guid.NewGuid() + ".jsonl"), DateTimeOffset.UtcNow);
        WaitFor(() => index.Count == 1, "the first transcript");
    }

    [Fact]
    public void Without_a_parent_folder_the_minute_timer_notices_the_projects_folder()
    {
        var root = _dir.Combine("claude", "projects");
        var files = new MemoryFileSystem(root) { RootExists = false };
        using var index = NewIndex(files, root, watch: false);
        Assert.Equal(0, index.Count);
        Assert.Equal(TranscriptIndexMode.WaitingForFolder, index.Mode);
        index.Tick();
        Assert.Equal(1, index.WalkCount); // still missing: no walk

        files.RootExists = true;
        files.Add(Path.Combine(root, "p", Guid.NewGuid() + ".jsonl"), 1, DateTime.UtcNow);
        index.Tick();
        WaitFor(() => index.Count == 1, "the walk once the folder exists");
        Assert.Equal(TranscriptIndexMode.Polling, index.Mode); // watch: false
    }

    [Fact]
    public void The_newest_write_is_kept_and_falls_back_when_the_newest_file_goes()
    {
        var older = Session("C--work-acme");
        var newer = Session("C--work-acme");
        WriteWorkedSession(older, DateTimeOffset.UtcNow);
        WriteWorkedSession(newer, DateTimeOffset.UtcNow);
        var olderTime = DateTime.UtcNow.AddMinutes(-30);
        File.SetLastWriteTimeUtc(older, olderTime);
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(-5));
        using var index = NewIndex();
        Assert.Equal(File.GetLastWriteTimeUtc(newer), index.MostRecentWriteUtc);

        File.Delete(newer);

        WaitFor(() => index.MostRecentWriteUtc == olderTime, "the next newest write");
    }

    [Fact]
    public void A_read_heals_a_missed_event_but_never_moves_a_write_back()
    {
        var files = new MemoryFileSystem(_dir.Path);
        var path = Path.Combine(_dir.Path, "p", Guid.NewGuid() + ".jsonl");
        var t0 = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);
        files.Add(path, 10, t0);
        using var index = NewIndex(files, watch: false);
        Assert.Equal(t0, index.MostRecentWriteUtc);

        index.Observe(new TranscriptFile(path, 20, t0.AddMinutes(1)));
        Assert.Equal(t0.AddMinutes(1), index.MostRecentWriteUtc);
        index.Observe(new TranscriptFile(path, 5, t0));
        Assert.Equal(20, index.Files().Single().Length);
        index.Observe(new TranscriptFile(Path.Combine(_dir.Path, "..", "elsewhere.jsonl"), 1, t0.AddHours(1)));
        index.Observe(new TranscriptFile(Path.Combine(_dir.Path, "p", "notes.txt"), 1, t0.AddHours(1)));
        Assert.Equal(1, index.Count);

        // A walk whose directory data lags keeps the later write it already saw.
        index.WalkNow();
        Assert.Equal(t0.AddMinutes(1), index.MostRecentWriteUtc);
    }

    [Fact]
    public async Task A_transcript_written_while_a_walk_runs_is_not_lost_when_the_walk_finishes()
    {
        Directory.CreateDirectory(_dir.Combine("C--work-acme")); // no folder event (and folder walk) during the race
        var files = new GatedFileSystem(DiskTranscriptFileSystem.Instance);
        using var index = NewIndex(files);
        Assert.Equal(0, index.Count);

        // The second walk reads the folder, then stalls before it is applied.
        files.Hold = true;
        var walk = Task.Run(index.WalkNow);
        Assert.True(files.Walking.Wait(Generous));
        var during = Session("C--work-acme");
        WriteWorkedSession(during, DateTimeOffset.UtcNow);
        WaitFor(() => index.Files().Any(f => f.Path == during), "the watcher's event");

        files.Release.Set();
        await walk.WaitAsync(Generous);
        Assert.Equal(2, index.WalkCount);
        Assert.Contains(index.Files(), f => f.Path == during); // the stale walk result didn't win
    }

    [Fact]
    public void Subagent_transcripts_are_never_listed_as_sessions()
    {
        var main = Session("C--work-acme");
        WriteWorkedSession(main, DateTimeOffset.UtcNow);
        var id = Path.GetFileNameWithoutExtension(main);
        // A subagent file with work of its own (and a GUID-like folder above it).
        Transcript.Write(_dir.Combine("C--work-acme", id, "subagents", Guid.NewGuid() + ".jsonl"),
            Transcript.Assistant(id, "msg_sub", DateTimeOffset.UtcNow, output: 100_000));
        using var sessions = new ClaudeSessionLogSource(NewIndex());
        var today = DateOnly.FromDateTime(DateTime.Now);

        Assert.Equal([id], sessions.GetSessionsForDate(today).Select(s => s.SessionId));
        Assert.Equal([id], sessions.Reader.GetSessionsForDate(today, onlyWithWork: false).Select(s => s.SessionId));
        Assert.Equal(2, sessions.Index.Count); // still counted as activity
    }

    [Fact]
    public void Disposing_stops_the_watcher_and_answers_nothing_after()
    {
        var index = NewIndex();
        Assert.Equal(0, index.Count);
        index.Dispose();
        WriteWorkedSession(Session("C--work-acme"), DateTimeOffset.UtcNow);
        Thread.Sleep(300);
        Assert.Equal(0, index.Count);
        Assert.True(index.IsDisposed);

        using var never = NewIndex();
        never.Dispose();
        Assert.Equal(0, never.Count); // never started: no walk after shutdown
        Assert.Equal(0, never.WalkCount);
    }
}

public class SessionParseCacheTests
{
    [Fact]
    public void The_parse_cache_drops_the_least_recently_read_transcript_first()
    {
        using var dir = new TempDir();
        var reader = Readers.For(dir);
        var now = DateTimeOffset.UtcNow;
        string Write(int n)
        {
            var id = Guid.NewGuid().ToString();
            var path = dir.Combine("p", id + ".jsonl");
            Transcript.Write(path, Transcript.Assistant(id, "m" + n, now, output: n + 1));
            return path;
        }
        var hot = Write(0);
        var paths = Enumerable.Range(1, 600).Select(Write).ToList();
        Assert.Equal(1, reader.ReadSession(hot)!.OutputTokens);
        // Its first byte broken: a parse from the start would lose its only line, so
        // the hot transcript must never be dropped and parsed again.
        using (var stream = new FileStream(hot, FileMode.Open, FileAccess.Write))
            stream.WriteByte((byte)'#');

        foreach (var path in paths)
        {
            Assert.NotNull(reader.ReadSession(path));
            Assert.NotNull(reader.ReadSession(hot)); // read on every refresh
        }

        Assert.Equal(ClaudeSessionLogReader.MaxCachedTranscripts, reader.CachedCount);
        Assert.True(reader.IsCached(hot));
        Assert.True(reader.IsCached(paths[^1]));
        Assert.False(reader.IsCached(paths[0]));
        Assert.True(reader.IsCached(paths[600 - ClaudeSessionLogReader.MaxCachedTranscripts + 1]));
        Assert.False(reader.IsCached(paths[600 - ClaudeSessionLogReader.MaxCachedTranscripts]));
        Assert.Equal(1, reader.ReadSession(hot)!.OutputTokens);
    }

    [Fact]
    public void A_cached_transcript_is_read_on_from_where_it_stopped()
    {
        using var dir = new TempDir();
        var reader = Readers.For(dir);
        var id = Guid.NewGuid().ToString();
        var path = dir.Combine("p", id + ".jsonl");
        Transcript.Write(path, Transcript.Assistant(id, "m1", DateTimeOffset.UtcNow, output: 10));
        Assert.Equal(10, reader.ReadSession(path)!.OutputTokens);

        // A byte that isn't JSON at the start would break a parse from offset 0.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.WriteByte((byte)'#');
        Transcript.Append(path, Transcript.Assistant(id, "m2", DateTimeOffset.UtcNow, output: 5) + "\n");

        Assert.Equal(15, reader.ReadSession(path)!.OutputTokens);
        Assert.Equal(5, Readers.For(dir).ReadSession(path)!.OutputTokens); // a new reader parses from the start
    }
}

public class SessionSourceOwnershipTests
{
    [Fact]
    public void Sessions_handed_to_the_default_graph_outlive_it_and_its_own_go_with_it()
    {
        using var dir = new TempDir();
        var settings = new AppSettings { ClaudeSource = "feed" };
        using var shared = new ClaudeSessionLogSource(dir.Combine("projects"));

        AppServices.CreateDefault(settings, shared).Dispose();
        Assert.False(shared.Index.IsDisposed);

        var own = new ClaudeSessionLogSource(dir.Combine("projects"));
        AppServices.Create(settings, own, dir.Combine("feed"), watchFeed: false, _ => throw new InvalidOperationException()).Dispose();
        Assert.True(own.Index.IsDisposed);
    }
}

/// <summary>The disk, with every enumeration and stat counted.</summary>
internal sealed class CountingFileSystem(ITranscriptFileSystem inner) : ITranscriptFileSystem
{
    private int _walks;
    public int Walks => Volatile.Read(ref _walks);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public List<TranscriptFile> Walk(string folder)
    {
        Interlocked.Increment(ref _walks);
        return inner.Walk(folder);
    }

    public TranscriptFile? Stat(string path) => inner.Stat(path);
}

/// <summary>The disk, with a walk that can be held after it read the folder (to race events against it).</summary>
internal sealed class GatedFileSystem(ITranscriptFileSystem inner) : ITranscriptFileSystem
{
    public volatile bool Hold;
    public ManualResetEventSlim Walking { get; } = new();
    public ManualResetEventSlim Release { get; } = new();

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public List<TranscriptFile> Walk(string folder)
    {
        var found = inner.Walk(folder);
        if (Hold)
        {
            Walking.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
        }
        return found;
    }

    public TranscriptFile? Stat(string path) => inner.Stat(path);
}

/// <summary>Transcripts that exist only in memory (no watcher event ever reports them).</summary>
internal sealed class MemoryFileSystem(string root) : ITranscriptFileSystem
{
    private readonly Dictionary<string, TranscriptFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool RootExists { get; set; } = true;
    public TimeSpan WalkDelay { get; init; }

    public void Add(string path, long length, DateTime lastWriteUtc)
    {
        lock (_files)
            _files[path] = new TranscriptFile(path, length, lastWriteUtc);
    }

    public bool DirectoryExists(string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            ? RootExists
            : Directory.Exists(path);

    public List<TranscriptFile> Walk(string folder)
    {
        if (WalkDelay > TimeSpan.Zero)
            Thread.Sleep(WalkDelay);
        lock (_files)
            return [.. _files.Values];
    }

    public TranscriptFile? Stat(string path)
    {
        lock (_files)
            return _files.TryGetValue(path, out var file) ? file : null;
    }
}

public class ChangeThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void A_burst_is_one_call_at_once_and_one_at_the_end_of_the_interval()
    {
        var calls = 0;
        using var throttle = new ChangeThrottle(TimeSpan.FromMilliseconds(300), () => Interlocked.Increment(ref calls), () => T0);

        throttle.Signal();
        Assert.Equal(1, Volatile.Read(ref calls));
        for (var i = 0; i < 10; i++)
            throttle.Signal();
        Assert.Equal(1, Volatile.Read(ref calls));

        WaitFor(() => Volatile.Read(ref calls) == 2, "the trailing call");
        Thread.Sleep(600);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void Signals_further_apart_than_the_interval_each_call_at_once()
    {
        var calls = 0;
        var now = T0;
        using var throttle = new ChangeThrottle(TimeSpan.FromSeconds(1), () => Interlocked.Increment(ref calls), () => now);
        for (var i = 1; i <= 3; i++)
        {
            throttle.Signal();
            Assert.Equal(i, Volatile.Read(ref calls));
            now += TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public void A_clock_set_back_delays_the_trailing_call_by_one_interval_at_most()
    {
        var calls = 0;
        var now = T0;
        using var throttle = new ChangeThrottle(TimeSpan.FromMilliseconds(200), () => Interlocked.Increment(ref calls), () => now);
        throttle.Signal();
        now = T0 - TimeSpan.FromHours(1); // the system clock goes back an hour
        throttle.Signal();

        WaitFor(() => Volatile.Read(ref calls) == 2, "the trailing call within the interval");
    }

    [Fact]
    public void Nothing_is_called_after_dispose()
    {
        var calls = 0;
        var throttle = new ChangeThrottle(TimeSpan.FromMilliseconds(200), () => Interlocked.Increment(ref calls), () => T0);
        throttle.Signal();
        throttle.Signal(); // waits for the end of the interval
        throttle.Dispose();
        Thread.Sleep(500);
        throttle.Signal();
        Assert.Equal(1, Volatile.Read(ref calls));
    }
}

public class TranscriptRefreshWiringTests
{
    [Fact]
    public void A_session_transcript_written_asks_for_a_refresh_and_a_subagent_file_does_not()
    {
        using var dir = new TempDir();
        var projects = dir.Combine("projects");
        Directory.CreateDirectory(projects);
        var sessions = new ClaudeSessionLogSource(projects);
        using var services = AppServices.Create(new AppSettings { ClaudeSource = "feed" }, sessions, dir.Combine("feed"),
            watchFeed: false, _ => throw new InvalidOperationException());
        var changes = 0;
        services.UsageChanged += () => Interlocked.Increment(ref changes);
        Assert.True(sessions.Index.WaitUntilReady(TimeSpan.FromSeconds(10)));

        Transcript.Write(Path.Combine(projects, "C--work-acme", Guid.NewGuid() + "-sub", "subagents", "agent-c3.jsonl"), "{}");
        Thread.Sleep(1500);
        Assert.Equal(0, Volatile.Read(ref changes));

        var id = Guid.NewGuid().ToString();
        var path = Path.Combine(projects, "C--work-acme", id + ".jsonl");
        Transcript.Write(path, Transcript.User(id, DateTimeOffset.UtcNow));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Volatile.Read(ref changes) == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.Equal(1, Volatile.Read(ref changes));

        // Claude Code answering: many appends, about one refresh a second.
        for (var i = 0; i < 20; i++)
            Transcript.Append(path, Transcript.Assistant(id, "m" + i, DateTimeOffset.UtcNow, output: 10) + "\n");
        Thread.Sleep(2500);
        Assert.InRange(Volatile.Read(ref changes), 2, 3);
    }
}
