using StickyAIusage.Health;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>What the panel and Settings → Health say while the usage service waits for
/// Claude Code to renew an expired token (the app only reads the sign-in).</summary>
public class TokenWaitTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Waiting(bool withNumbers) => new()
    {
        FiveHourUtilizationPercent = withNumbers ? 43 : null,
        WeeklyUtilizationPercent = withNumbers ? 14 : null,
        FetchedAt = Now.AddMinutes(-20),
        IsStale = true,
        SourceId = ClaudeOAuthUsageSource.Id,
        Trust = SourceTrust.Unofficial,
        StatusDetail = ClaudeOAuthUsageSource.TokenExpiredDetail
    };

    [Fact]
    public void With_numbers_the_panel_says_how_old_they_are_and_what_to_do()
    {
        var line = HealthLine.For(Waiting(withNumbers: true), notice: null, Now)!;

        Assert.Equal(UsageHealth.Stale, line.Health);
        Assert.Equal("Last updated 20m ago. " + ClaudeOAuthUsageSource.TokenExpiredDetail, line.Detail);
        Assert.Equal(HealthAction.OpenSettings, line.Action);
        Assert.DoesNotContain("log", line.Detail);
    }

    [Fact]
    public void Without_numbers_the_panel_says_the_same_instead_of_a_failure()
    {
        var line = HealthLine.For(Waiting(withNumbers: false), notice: null, Now)!;

        Assert.Equal(UsageHealth.Unavailable, line.Health);
        Assert.Equal(ClaudeOAuthUsageSource.TokenExpiredDetail, line.Detail);
        Assert.Equal(HealthAction.OpenSettings, line.Action);
    }

    [Fact]
    public void Health_warns_with_the_numbers_their_age_and_a_way_to_the_plugin()
    {
        var item = HealthCheck.Build(HealthCheckTests.Healthy() with
        {
            Now = Now, Usage = Waiting(withNumbers: true), Mode = ClaudeSourceMode.Legacy
        }).Single(i => i.Id == "limits");

        Assert.Equal(HealthStatus.Warn, item.Status);
        Assert.Contains("last updated 20m ago", item.Detail);
        Assert.EndsWith(ClaudeOAuthUsageSource.TokenExpiredDetail, item.Detail);
        Assert.Equal(HealthActionIds.OpenUsageSource, item.ActionId);
    }

    [Fact]
    public void The_words_name_the_fix_and_promise_nothing_is_changed()
    {
        Assert.Contains("only reads it", ClaudeOAuthUsageSource.TokenExpiredDetail);
        Assert.Contains("Start Claude Code once", ClaudeOAuthUsageSource.TokenExpiredDetail);
        Assert.Contains("never renewed or changed", UsageSourceOptions.UnofficialWarning);
        Assert.DoesNotContain("saves it back", UsageSourceOptions.UnofficialWarning);
    }
}
