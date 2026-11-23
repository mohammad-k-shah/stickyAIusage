using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// An AI product whose account-level usage the widget shows in the
/// provider-neutral shape. Claude (<see cref="ClaudeUsageProvider"/>) also
/// feeds the Claude-shaped layouts (<see cref="IUsageProvider"/>); another
/// tool (<see cref="CodexUsageProvider"/>) is one class implementing this,
/// shown as its own section in the flyout.
/// </summary>
public interface IProviderUsageReader
{
    /// <summary>Short id used in settings/config, e.g. "claude", "codex".</summary>
    string ProviderId { get; }

    /// <summary>Display name for the UI, e.g. "Claude".</summary>
    string DisplayName { get; }

    /// <summary>Whether the tool is on this machine at all; a provider that isn't
    /// leaves no trace in the UI. Cheap: asked on every refresh.</summary>
    bool IsPresent { get; }

    /// <summary>The current usage in the provider-neutral shape; never throws.</summary>
    Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Claude provider: the provider-neutral reading plus the Claude-shaped
/// snapshot the monthly spend and plan-limit layouts read.
/// </summary>
public interface IUsageProvider : IProviderUsageReader
{
    bool IProviderUsageReader.IsPresent => true;

    /// <summary>
    /// The current account usage in the Claude-shaped snapshot the monthly
    /// spend and plan-limit layouts read. Implementations are responsible
    /// for their own rate limiting so the underlying API is never called more
    /// often than the provider's own polling contract allows (at most once
    /// per 30s for Claude).
    /// </summary>
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
