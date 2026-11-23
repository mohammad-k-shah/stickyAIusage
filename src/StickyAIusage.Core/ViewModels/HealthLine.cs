using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.ViewModels;

/// <summary>What the health line's button does.</summary>
public enum HealthAction
{
    /// <summary>Nothing to do: the numbers come back by themselves.</summary>
    None,

    /// <summary>Opens the folder with diagnostics.log, which has the reason.</summary>
    OpenLogFolder,

    /// <summary>Opens Settings (where the usage source was turned off).</summary>
    OpenSettings,

    /// <summary>Opens Settings → Health, which says what is missing and what to do.</summary>
    OpenHealth
}

/// <summary>
/// The line at the top of the flyout's usage card when the reading isn't
/// current: why, in a sentence, and at most one button. A pill or tooltip
/// never just points at a log folder. Shown only when no flyout notice
/// (<see cref="FlyoutNotice"/>) already says what to do, so the flyout never
/// gives two answers to one problem.
/// </summary>
public sealed record HealthLine(UsageHealth Health, string Detail, HealthAction Action)
{
    public const string FailedDetail =
        "Couldn't read your usage. Health in Settings checks what is missing and says what to do.";

    /// <summary>The button's text; null without a button.</summary>
    public string? ActionLabel => Action switch
    {
        HealthAction.OpenLogFolder => "Open log folder",
        HealthAction.OpenSettings => "Open Settings",
        HealthAction.OpenHealth => "Open Health",
        _ => null
    };

    /// <summary>The line for this reading, or null when it is current or a
    /// notice already says what to do (sign in again, install the plugin).
    /// The plugin tip answers nothing about the reading, so the line stays
    /// beside it.</summary>
    public static HealthLine? For(UsageSnapshot usage, FlyoutNotice? notice, DateTimeOffset now)
    {
        if (notice is { Kind: FlyoutNoticeKind.SignIn or FlyoutNoticeKind.InstallPlugin })
            return null;

        var health = usage.ToProviderUsage().Health;
        var age = Format.Ago(now - usage.FetchedAt);
        switch (health)
        {
            case UsageHealth.NeedsSignIn:
                // Every sign-in state has the sign-in notice; this is the
                // fallback should that ever change.
                return new HealthLine(health, FlyoutNotice.SignInMessage, HealthAction.None);
            case UsageHealth.Unavailable:
                return usage.StatusDetail switch
                {
                    AppServices.UsageOffDetail => new HealthLine(health, usage.StatusDetail, HealthAction.OpenSettings),
                    // Not a failure: the limits come with Claude Code's next response.
                    ClaudeFeedUsageSource.NoLimitsDetail or ClaudeFeedUsageSource.WaitingDetail =>
                        new HealthLine(health, usage.StatusDetail, HealthAction.None),
                    ClaudeOAuthUsageSource.TokenExpiredDetail => new HealthLine(health, usage.StatusDetail, HealthAction.OpenSettings),
                    _ => new HealthLine(health, FailedDetail, HealthAction.OpenHealth)
                };
            case UsageHealth.Stale when usage.StatusDetail == ClaudeOAuthUsageSource.TokenExpiredDetail:
                // Not failing: the numbers wait for Claude Code to renew its token.
                return new HealthLine(health, $"Last updated {age}. {usage.StatusDetail}", HealthAction.OpenSettings);
            case UsageHealth.Stale:
                return new HealthLine(health,
                    $"Last updated {age}: the last try didn't get new numbers. It retries by itself; the log in the log folder says why.",
                    HealthAction.OpenLogFolder);
            default:
                // The plugin's feed only moves while Claude Code answers: an
                // older reading isn't failing, it is as of then.
                return WidgetViewModel.IsFeedReadingAsOf(usage, now)
                    ? new HealthLine(UsageHealth.Ok, $"As of {age}: these numbers update while Claude Code is active.", HealthAction.None)
                    : null;
        }
    }
}
