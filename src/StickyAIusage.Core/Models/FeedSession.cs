namespace StickyAIusage.Models;

/// <summary>
/// One Claude Code session as the companion plugin last described it in the
/// feed folder (<c>claude-&lt;sessionId&gt;.json</c>). The account-wide plan
/// limits become a <see cref="UsageSnapshot"/>; the per-session parts are kept
/// here for the features that build on them (attention lights, exact session
/// costs).
/// </summary>
/// <param name="ObservedAt">When the plugin last wrote the record (its own clock).</param>
/// <param name="RateLimitsAt">When Claude Code last reported the limits in it:
/// the end of a turn or a measurement, never a heartbeat. Null before the
/// session's first response.</param>
/// <param name="State">"started", "working", "needs_you", "done", "idle", "rate_limited" or "ended"
/// (an open set: plugin 2.1 wrote "idle" after every turn).</param>
/// <param name="Attention">What a "needs_you" session waits for; null otherwise.</param>
/// <param name="LastTurn">How long the last finished turn took (plugin 2.2+).</param>
public sealed record FeedSession(
    string SessionId,
    string? Surface,
    string? Cwd,
    string? Model,
    string? ClaudeCodeVersion,
    string? PluginVersion,
    DateTimeOffset ObservedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? RateLimitsAt,
    IReadOnlyList<UsageWindow>? RateLimits,
    long? ContextTokens,
    long? ContextWindow,
    double? ContextPercent,
    decimal? CostUsd,
    string? State,
    DateTimeOffset? StateSince,
    FeedAttention? Attention,
    TimeSpan? LastTurn = null)
{
    public bool HasEnded => string.Equals(State, "ended", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a waiting session waits for, as the plugin records it: the
/// kind ("permission", "question", "input", or one a later plugin adds) and,
/// for a permission, the tool's name. Never a prompt, command or path.</summary>
public sealed record FeedAttention(string Type, string? Tool);
