using StickyAIusage.Forecasting;
using StickyAIusage.Models;

namespace StickyAIusage.Tests;

public class UsageJournalTests : IDisposable
{
    private readonly TempDir _dir = new();
    private DateTimeOffset _now = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 11, 17, 16, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private string JournalPath => _dir.Combine("cache", "journal.jsonl");

    private UsageJournal Open() => new(JournalPath, () => _now);

    private readonly DateTimeOffset _start = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);

    /// <summary>Moves the clock to <paramref name="minutes"/> after the start; readings are never ahead of it.</summary>
    private DateTimeOffset At(double minutes) => _now = _start.AddMinutes(minutes);

    private static UsageSample Sample(double percent, DateTimeOffset at, DateTimeOffset? reset = null, string kind = UsageWindowKinds.FiveHour) =>
        new("claude", kind, reset ?? Reset, percent, at);

    private static ProviderUsage Reading(DateTimeOffset at, double fiveHour, double weekly) =>
        new UsageSnapshot
        {
            FiveHourUtilizationPercent = fiveHour,
            FiveHourResetsAt = Reset,
            WeeklyUtilizationPercent = weekly,
            WeeklyResetsAt = Reset.AddDays(3),
            FetchedAt = at
        }.ToProviderUsage();

    [Fact]
    public void Every_window_of_a_reading_becomes_a_sample()
    {
        var journal = Open();

        Assert.True(journal.Record(Reading(_now, 43, 14)));

        Assert.Equal(43, Assert.Single(journal.Samples("claude", UsageWindowKinds.FiveHour)).PercentUsed);
        var weekly = Assert.Single(journal.Samples("claude", UsageWindowKinds.SevenDay));
        Assert.Equal(Reset.AddDays(3), weekly.ResetsAt);
        Assert.Equal(_now, weekly.ObservedAt);
    }

    [Fact]
    public void The_same_observation_read_again_adds_nothing()
    {
        var journal = Open();
        journal.Record(Reading(_now, 43, 14));

        // The next tick returns the same cached reading.
        Assert.False(journal.Record(Reading(_now, 43, 14)));
        // An older reading arriving late (another source) too.
        Assert.False(journal.Record(Reading(_now.AddMinutes(-1), 40, 14)));

        Assert.Single(journal.Samples("claude", UsageWindowKinds.FiveHour));
    }

    [Fact]
    public void An_unchanged_percentage_is_kept_only_every_five_minutes()
    {
        var journal = Open();
        journal.Record(Sample(43, _now));

        Assert.False(journal.Record(Sample(43, At(4))));
        Assert.True(journal.Record(Sample(44, At(4.5))));
        Assert.False(journal.Record(Sample(44, At(9))));
        Assert.True(journal.Record(Sample(44, At(9.5))));

        Assert.Equal([43, 44, 44], journal.Samples("claude", UsageWindowKinds.FiveHour).Select(s => s.PercentUsed));
    }

    [Fact]
    public void A_new_reset_time_is_kept_even_at_the_same_percentage()
    {
        var journal = Open();
        journal.Record(Sample(0, _now));

        Assert.True(journal.Record(Sample(0, _now.AddMinutes(1), Reset.AddHours(5))));
    }

    [Fact]
    public void A_restart_reads_the_samples_back()
    {
        var journal = Open();
        journal.Record(Reading(_now, 43, 14));
        journal.Record(Reading(_now.AddMinutes(1), 45, 14.5));

        var reopened = Open();

        Assert.Equal([43, 45], reopened.Samples("claude", UsageWindowKinds.FiveHour).Select(s => s.PercentUsed));
        Assert.Equal(Reset, reopened.Samples("claude", UsageWindowKinds.FiveHour)[0].ResetsAt);
        // And dedup continues where it was.
        Assert.False(reopened.Record(Reading(_now.AddMinutes(1), 45, 14.5)));
    }

    [Fact]
    public void Broken_lines_and_other_schemas_are_skipped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        File.WriteAllLines(JournalPath,
        [
            UsageJournal.Serialize(Sample(40, _now.AddMinutes(-2))),
            "{\"schema\":1,\"provider\":\"claude\",\"kind\":\"five_hour\",\"percent\":4",   // cut off
            "{\"schema\":2,\"provider\":\"claude\",\"kind\":\"five_hour\",\"percent\":50,\"at\":\"2026-11-17T13:59:00Z\"}",
            "{\"schema\":1,\"provider\":\"claude\",\"kind\":\"five_hour\",\"percent\":\"x\",\"at\":\"2026-11-17T13:59:00Z\"}",
            "not json",
            "",
            UsageJournal.Serialize(Sample(42, _now.AddMinutes(-1)))
        ]);

        var journal = Open();

        Assert.Equal([40, 42], journal.Samples("claude", UsageWindowKinds.FiveHour).Select(s => s.PercentUsed));
    }

    [Fact]
    public void A_sample_without_a_reset_round_trips()
    {
        var line = UsageJournal.Serialize(new UsageSample("claude", "five_hour", null, 12.345, _now));
        var back = UsageJournal.Parse(line);

        Assert.Equal(new UsageSample("claude", "five_hour", null, 12.345, _now), back);
    }

    [Fact]
    public void Old_samples_are_dropped_and_the_file_is_rewritten()
    {
        var journal = Open();
        journal.Record(Sample(10, _now.AddDays(-9)));                                       // too old
        journal.Record(Sample(20, _now.AddDays(-7)));                                       // kept
        journal.Record(Sample(30, _now.AddDays(-20), Reset, UsageWindowKinds.MonthSpend));  // spend: 35 days
        journal.Record(Sample(40, _now.AddDays(-36), Reset, UsageWindowKinds.SpendLimit));  // too old even for spend

        var reopened = Open(); // a load prunes

        Assert.Equal([20], reopened.Samples("claude", UsageWindowKinds.FiveHour).Select(s => s.PercentUsed));
        Assert.Single(reopened.Samples("claude", UsageWindowKinds.MonthSpend));
        Assert.Empty(reopened.Samples("claude", UsageWindowKinds.SpendLimit));
        Assert.Equal(2, File.ReadAllLines(JournalPath).Length);
    }

    [Fact]
    public void A_full_file_is_compacted_to_the_newest_samples()
    {
        var journal = Open();
        for (var i = 0; i <= UsageJournal.MaxLines; i++)
            journal.Record(Sample(i % 100, _now.AddMinutes(-UsageJournal.MaxLines + i)));

        var lines = File.ReadAllLines(JournalPath);
        Assert.True(lines.Length <= UsageJournal.MaxLines, $"{lines.Length} lines");
        Assert.Equal(UsageJournal.KeepWhenFull, lines.Length);
        // The newest sample survived; the oldest went.
        var samples = journal.Samples("claude", UsageWindowKinds.FiveHour);
        Assert.Equal(_now, samples[^1].ObservedAt);
        Assert.Equal(UsageJournal.KeepWhenFull, samples.Count);
        Assert.Equal(UsageJournal.KeepWhenFull, Open().Samples("claude", UsageWindowKinds.FiveHour).Count);
    }

    [Fact]
    public void A_folder_that_cannot_be_written_never_throws()
    {
        // A file where the cache folder should be: every write fails.
        File.WriteAllText(_dir.Combine("cache"), "");
        var journal = Open();

        Assert.True(journal.Record(Sample(40, _now)));
        Assert.Single(journal.Samples("claude", UsageWindowKinds.FiveHour));
    }

    [Fact]
    public void A_clock_set_back_does_not_freeze_the_journal()
    {
        var journal = Open();
        journal.Record(Sample(40, _now));
        journal.Record(Sample(45, _now = _now.AddMinutes(5)));

        _now = _now.AddHours(-1); // the clock goes back an hour
        Assert.True(journal.Record(Sample(41, _now)));

        Assert.Equal([41], journal.Samples("claude", UsageWindowKinds.FiveHour).Select(s => s.PercentUsed));
        Assert.Equal([41], Open().Samples("claude", UsageWindowKinds.FiveHour).Select(s => s.PercentUsed));
    }

    [Fact]
    public void A_small_clock_skew_keeps_the_samples()
    {
        var journal = Open();
        journal.Record(Sample(40, _now.AddSeconds(90))); // a source whose clock runs 90 s ahead

        Assert.False(journal.Record(Sample(40, _now.AddSeconds(30))));
        Assert.Single(journal.Samples("claude", UsageWindowKinds.FiveHour));
    }

    [Fact]
    public void The_file_is_read_on_first_use_not_when_the_journal_is_built()
    {
        var journal = Open();
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        File.WriteAllText(JournalPath, UsageJournal.Serialize(Sample(40, _now)) + "\n");

        Assert.Single(journal.Samples("claude", UsageWindowKinds.FiveHour));
    }

    [Fact]
    public void Not_a_number_is_ignored()
    {
        var journal = Open();
        Assert.False(journal.Record(Sample(double.NaN, _now)));
        Assert.Empty(journal.Samples("claude", UsageWindowKinds.FiveHour));
    }
}
