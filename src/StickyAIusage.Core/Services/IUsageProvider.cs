using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Abstraction over "an AI product whose account-level usage/spend we can poll".
/// v1 ships only <see cref="ClaudeUsageProvider"/>; this interface exists so that
/// v2 (Copilot / Codex / ChatGPT) can be added without
/// reworking the docking window, tray icon, or polling loop.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Short id used in settings/config, e.g. "claude".</summary>
    string ProviderId { get; }

    /// <summary>Display name for the UI, e.g. "Claude".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Fetches (or returns a cached copy of) the current account usage snapshot.
    /// Implementations are responsible for their own rate limiting so the
    /// underlying API is never called more often than the provider's own
    /// polling contract allows (at most once per 30s for Claude).
    /// </summary>
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
