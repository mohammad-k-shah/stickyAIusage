using System.Globalization;
using StickyAIusage.Models;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(950, "950")]
    [InlineData(1_000, "1k")]
    [InlineData(15_800, "15.8k")]
    [InlineData(247_309, "247k")]
    [InlineData(999_960, "1M")]
    [InlineData(3_300_821, "3.3M")]
    [InlineData(1_000_000, "1M")]
    public void Compact_numbers(long n, string expected) => Assert.Equal(expected, Format.Compact(n));

    [Theory]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]
    [InlineData("claude-3-5-sonnet-20241022", "Sonnet 3.5")]
    [InlineData("claude-opus-4-6[1m]", "Opus 4.6")]
    [InlineData(null, "Unknown model")]
    public void Model_display_names(string? id, string expected) => Assert.Equal(expected, Format.ModelName(id));

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(7 * 60, "7m")]
    [InlineData(103 * 60, "1h 43m")]
    [InlineData(2 * 3600, "2h")]
    [InlineData(51 * 3600, "2d 3h")]
    public void Durations(int seconds, string expected) =>
        Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Money_and_percent()
    {
        Assert.Equal("$3.34", Format.Usd(3.3387m));
        Assert.Equal("<$0.01", Format.Usd(0.004m));
        Assert.Equal("$0.00", Format.Usd(0m));
        Assert.Equal("63.9%", Format.Percent(63.87));
        Assert.Equal("100%", Format.Percent(99.97));
        Assert.Equal("Extra high", Format.Effort("xhigh"));
        Assert.Equal("Medium", Format.Effort("medium"));
    }
}

public class SessionCardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(GitInfo? git = null, long contextTokens = 247_309, string? name = null) => new()
    {
        SessionId = "3c8e62c4-17a7-453e-8385-88147c5aaf07",
        SessionName = name,
        TranscriptPath = "/x.jsonl",
        WorkingDirectory = @"C:\dev\acme-webapp",
        Model = "claude-sonnet-5",
        ThinkingEffort = "medium",
        ClaudeCodeVersion = "2.1.273",
        StartedAt = Now.AddMinutes(-7),
        LastActivityAt = Now.AddSeconds(-30),
        InputTokens = 50,
        OutputTokens = 39_016,
        CacheReadTokens = 3_300_821,
        CacheCreationTokens = 469_573,
        ContextWindowSize = 1_000_000,
        CurrentContextTokens = contextTokens,
        EstimatedCostUsd = 3.34m,
        Git = git
    };

    [Fact]
    public void Segment_rows_cover_the_default_fields()
    {
        var vm = SessionCardViewModel.From(Session(), Now);

        Assert.Equal(
            [
                ["Model: Sonnet 5", "Effort: Medium"],
                ["In: 50", "Out: 39k", "Cached: 3.8M", "Total: 3.8M"],
                ["Cache Hit: 87.5%", "Cache Read: 3.3M (86.6%)", "Cache Write: 470k (12.3%)"]
            ],
            vm.Rows.Select(r => r.Segments.Select(s => $"{s.Label}: {s.Value}")));
        Assert.Equal("$3.34", vm.CostText);
        Assert.Equal("acme-webapp", vm.Title);
        Assert.Equal("#3c8e62c4 · ran 6m · last active just now", vm.Subtitle);
        Assert.True(vm.IsLive);
        Assert.Equal("Claude Code 2.1.273", vm.FooterText);
    }

    [Fact]
    public void Each_row_numbers_its_segments_so_colors_cycle_and_only_the_first_is_flat()
    {
        var vm = SessionCardViewModel.From(Session(), Now);

        foreach (var row in vm.Rows.Select(r => r.Segments))
        {
            Assert.Equal(Enumerable.Range(0, row.Count), row.Select(s => s.ColorIndex));
            Assert.Equal(Enumerable.Range(0, row.Count).Select(i => i == 0), row.Select(s => s.IsFirst));
        }
    }

    [Theory]
    [InlineData(247_309, ContextLevel.Ok, "247k / 1M · 24.7%")]
    [InlineData(650_000, ContextLevel.Warn, "650k / 1M · 65%")]
    [InlineData(900_000, ContextLevel.High, "900k / 1M · 90%")]
    public void Context_level_and_text(long tokens, ContextLevel level, string text)
    {
        var vm = SessionCardViewModel.From(Session(contextTokens: tokens), Now);
        Assert.Equal(level, vm.ContextLevel);
        Assert.Equal(text, vm.ContextText);
    }

    [Fact]
    public void Git_row_summarises_state_changes_and_sync()
    {
        var git = new GitInfo
        {
            Branch = "feature/tasks", StagedCount = 2, UnstagedCount = 1, UntrackedCount = 5,
            Insertions = 88, Deletions = 3, AheadCount = 1, OriginOwner = "acme", OriginRepo = "webapp",
            PullRequestStatus = "OPEN", CiStatus = "SUCCESS"
        };
        var vm = SessionCardViewModel.From(Session(git), Now);

        Assert.True(vm.HasGit);
        Assert.Equal("2 staged · 1 modified · 5 untracked", vm.GitStateText);
        Assert.Equal(("+88", "−3"), (vm.GitAddedText, vm.GitRemovedText));
        Assert.Equal("↑1 ↓0", vm.GitSyncText);
        Assert.Equal("acme/webapp", vm.GitRepoText);
        Assert.Equal("PR open · CI success", vm.GitPrText);
    }

    [Fact]
    public void Named_session_shows_project_in_subtitle_and_signature_tracks_changes()
    {
        var a = SessionCardViewModel.From(Session(name: "Refactor payment retries"), Now);
        var b = SessionCardViewModel.From(Session(name: "Refactor payment retries"), Now);
        var c = SessionCardViewModel.From(Session(name: "Refactor payment retries", contextTokens: 1), Now);

        Assert.Equal("Refactor payment retries", a.Title);
        Assert.StartsWith("acme-webapp · #3c8e62c4", a.Subtitle);
        Assert.Equal(a.Signature, b.Signature);
        Assert.NotEqual(a.Signature, c.Signature);
    }
}

public class ThresholdsTests
{
    private static Thresholds.Result V(string y, string o, string r) =>
        Thresholds.Validate(y, o, r, CultureInfo.InvariantCulture);

    [Fact]
    public void Ascending_values_between_1_and_100_are_valid()
    {
        var r = V("50", "80", "95%");
        Assert.True(r.IsValid);
        Assert.Equal((50d, 80d, 95d), (r.Yellow, r.Orange, r.Red));
    }

    [Theory]
    [InlineData("90", "80", "95", Thresholds.Field.Yellow | Thresholds.Field.Orange)]
    [InlineData("50", "96", "95", Thresholds.Field.Orange | Thresholds.Field.Red)]
    [InlineData("abc", "80", "95", Thresholds.Field.Yellow)]
    [InlineData("50", "80", "101", Thresholds.Field.Red)]
    [InlineData("0", "80", "95", Thresholds.Field.Yellow)]
    public void Invalid_input_flags_the_offending_fields(string y, string o, string r, Thresholds.Field fields)
    {
        var result = V(y, o, r);
        Assert.False(result.IsValid);
        Assert.Equal(fields, result.InvalidFields);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }
}
