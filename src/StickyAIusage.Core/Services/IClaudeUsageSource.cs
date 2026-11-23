using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// One place Claude's account usage can be read from: the usage endpoint
/// today, the companion plugin's feed next. <see cref="ClaudeUsageProvider"/>
/// asks its sources in order of trust and picks one reading. A source keeps
/// its own polling contract: being asked never means a request must go out.
/// </summary>
public interface IClaudeUsageSource
{
    /// <summary>Short id for logs and settings, e.g. "claude-oauth".</summary>
    string SourceId { get; }

    SourceTrust Trust { get; }

    /// <summary>The source's current reading (fresh or kept from before), or
    /// null when it has nothing at all. Never throws for an expected failure
    /// (network, sign-in, a missing file); the provider still guards against one.</summary>
    Task<UsageSnapshot?> TryGetAsync(CancellationToken cancellationToken);
}
