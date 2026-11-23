using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>Every file the app rewrites while it runs is replaced in one
/// step: a reader running at the same time (the next start, the plugin, a
/// second copy) sees the old content or the new, never an empty or half file.</summary>
public class AtomicWriteTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Writes <paramref name="write"/> in a loop on another thread
    /// while this one reads the file and checks every read with
    /// <paramref name="isWhole"/>. A read the writer's rename blocks is
    /// simply tried again, and a write this tight loop of reads keeps
    /// blocked past its retries leaves the old file (the app's writers are
    /// best-effort and log it); only what a reader sees counts.</summary>
    private static void ReadWhileWriting(string path, Action<int> write, Func<string, bool> isWhole)
    {
        write(0);
        using var stop = new CancellationTokenSource();
        var written = 0;
        var writer = Task.Run(() =>
        {
            for (var i = 1; i < 200 && !stop.IsCancellationRequested; i++)
            {
                try
                {
                    write(i);
                    written++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Blocked by the reader the whole time: the old content stays.
                }
            }
        });

        var reads = 0;
        var broken = new List<string>();
        // A regression must fail, not hang the run.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!writer.IsCompleted || reads < 50) && DateTime.UtcNow < deadline)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; } // Windows: the target is being replaced
            reads++;
            if (!isWhole(text))
                broken.Add(text.Length > 40 ? text[..40] : text);
            if (writer.IsCompleted && reads >= 50)
                break;
            // Like a real reader, not one that never lets go: Windows can't
            // replace a file while it is open, so the writer would only wait.
            Thread.Sleep(1);
        }
        stop.Cancel();
        Assert.True(writer.Wait(TimeSpan.FromSeconds(20)), $"the writer did not finish ({written} writes)");

        Assert.True(reads >= 50, $"only {reads} reads");
        Assert.True(written >= 100, $"only {written} writes went through");
        Assert.Empty(broken);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [Fact]
    public void Bytes_are_written_as_given_into_a_new_folder_and_replace_the_old_file()
    {
        var path = _dir.Combine("Pictures", "usage-20261118-1400.png");
        byte[] first = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF], second = [1, 2, 3];
        AtomicFile.WriteAllBytes(path, first);
        Assert.Equal(first, File.ReadAllBytes(path));
        AtomicFile.WriteAllBytes(path, second);
        Assert.Equal(second, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void The_usage_cache_is_never_seen_half_written()
    {
        var path = _dir.Combine("cache", "claude-usage.json");
        ReadWhileWriting(path,
            i => ClaudeOAuthUsageSource.WriteDiskCache(path, new UsageSnapshot
            {
                HasMonthlySpend = true,
                MonthlySpendUsedUsd = i,
                MonthlySpendLimitUsd = 200,
                // A long value makes a partial read likely without the rename.
                SubscriptionType = new string('x', 20_000 + i)
            }),
            text => IsJson(text) && JsonSerializer.Deserialize<UsageSnapshot>(text) is { HasMonthlySpend: true });
    }

    [Fact]
    public void The_throttle_file_is_never_seen_half_written()
    {
        var path = _dir.Combine("cache", "claude-usage-throttle.json");
        var at = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        ReadWhileWriting(path,
            i => ClaudeOAuthUsageSource.WriteThrottleState(path, at.AddSeconds(i), TimeSpan.FromSeconds(30 + i)),
            text => text.Split('\n') is [var first, var second] &&
                    long.TryParse(first.Trim(), out _) && long.TryParse(second.Trim(), out _));
    }

    [Fact]
    public void The_day_target_state_is_never_seen_half_written()
    {
        var path = _dir.Combine("cache", "daily-spend.json");
        var today = new DateOnly(2026, 11, 17);
        ReadWhileWriting(path,
            i => DailySpendTracker.Save(path, new DailySpendTracker.State(today, 100, 0, 100 + i)),
            text => IsJson(text) && JsonSerializer.Deserialize<DailySpendTracker.State>(text) is { Baseline: 100 });
    }

    [Fact]
    public void Settings_are_never_seen_half_written()
    {
        var path = _dir.Combine("settings.json");
        ReadWhileWriting(path,
            i => SettingsService.Save(new AppSettings { DayTargetUsd = i, CardFields = new string('m', 20_000) }, path),
            text => IsJson(text) && JsonSerializer.Deserialize<AppSettings>(text) is { CardFields.Length: 20_000 });
    }

    /// <summary>Deterministic, where the race above is too short to catch on
    /// a small file: a save replaces the file rather than rewriting it in
    /// place, so a reader that opened the old file still reads the old
    /// content to the end (a rewrite would truncate it under the reader).</summary>
    private static void SaveReplacesTheFile(string path, Action writeOld, Action writeNew, string oldMarker)
    {
        writeOld();
        string seen;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            writeNew();
            using var reader = new StreamReader(held);
            seen = reader.ReadToEnd();
        }
        Assert.True(seen.Contains(oldMarker), path + " => " + seen);
    }

    [Fact]
    public void Small_files_are_replaced_not_rewritten_in_place()
    {
        var daily = _dir.Combine("cache", "daily-spend.json");
        var today = new DateOnly(2026, 11, 17);
        SaveReplacesTheFile(daily,
            () => DailySpendTracker.Save(daily, new DailySpendTracker.State(today, 111, 0, 111)),
            () => DailySpendTracker.Save(daily, new DailySpendTracker.State(today, 222, 0, 222)),
            "111");

        var throttle = _dir.Combine("cache", "claude-usage-throttle.json");
        var at = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        SaveReplacesTheFile(throttle,
            () => TryWrite(() => ClaudeOAuthUsageSource.WriteThrottleState(throttle, at, TimeSpan.FromSeconds(111))),
            () => TryWrite(() => ClaudeOAuthUsageSource.WriteThrottleState(throttle, at, TimeSpan.FromSeconds(222))),
            "111");

        var cache = _dir.Combine("cache", "claude-usage.json");
        SaveReplacesTheFile(cache,
            () => TryWrite(() => ClaudeOAuthUsageSource.WriteDiskCache(cache, new UsageSnapshot { MonthlySpendUsedUsd = 111 })),
            () => TryWrite(() => ClaudeOAuthUsageSource.WriteDiskCache(cache, new UsageSnapshot { MonthlySpendUsedUsd = 222 })),
            "111");
    }

    /// <summary>The app's callers swallow a failed write (best effort); so does
    /// this, when Windows keeps refusing to replace a file a reader holds.</summary>
    private static void TryWrite(Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_reader_holding_the_file_open_delays_a_save_instead_of_failing_it()
    {
        var path = _dir.Combine("settings.json");
        AtomicFile.WriteAllText(path, "old");
        // Open the way File.ReadAllText opens it: no share-delete, so Windows
        // refuses the replace (access denied) until the reader closes it.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Its own thread, not a thread-pool continuation: with the whole suite running in
        // parallel a continuation could start after the save's 500 ms of retries (CI, 2026-11-20).
        var closer = new Thread(() =>
        {
            Thread.Sleep(150);
            reader.Dispose();
        }) { IsBackground = true };
        closer.Start();

        AtomicFile.WriteAllText(path, "new");

        await Task.Run(closer.Join);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void A_save_killed_before_its_rename_leaves_the_previous_settings_readable()
    {
        var path = _dir.Combine("settings.json");
        SettingsService.Save(new AppSettings { DayTargetUsd = 7 }, path);
        // What a process killed mid-save leaves behind: its temp file, half written.
        File.WriteAllText(path + "." + Guid.NewGuid().ToString("N") + ".tmp", "{ \"DayTargetUsd\": 9");

        Assert.Equal(7, SettingsService.Load(path).DayTargetUsd);
    }

    [Fact]
    public void The_day_target_state_survives_a_restart()
    {
        var path = _dir.Combine("cache", "daily-spend.json");
        var today = new DateOnly(2026, 11, 17);
        new DailySpendTracker(path).Record(100m, today, () => 0m);

        var spent = new DailySpendTracker(path).Record(104.5m, today, () => 0m);

        Assert.Equal(4.5m, spent);
        Assert.Empty(Directory.GetFiles(_dir.Combine("cache"), "*.tmp"));
    }
}
