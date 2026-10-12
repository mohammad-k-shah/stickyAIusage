namespace StickyAIusage.Models;

/// <summary>
/// One Claude Desktop Code-tab session, reconstructed from its .jsonl transcript
/// under %USERPROFILE%\.claude\projects\**\*.jsonl.
/// </summary>
public sealed class SessionInfo
{
    public required string SessionId { get; init; }
    public string? SessionName { get; init; }
    public required string TranscriptPath { get; init; }
    public required string WorkingDirectory { get; init; }

    public string? Model { get; init; }
    public string? ThinkingEffort { get; init; }
    public string? ClaudeCodeVersion { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

    /// <summary>Model's max context window size in tokens, looked up by model id
    /// (see Services/ModelContextWindows.cs). Null if the model is unrecognized.</summary>
    public long? ContextWindowSize { get; init; }

    /// <summary>Tokens currently occupying context (most recent turn's usage),
    /// as opposed to TotalTokens which accumulates across the whole session.</summary>
    public long CurrentContextTokens { get; init; }

    public int CompactionCount { get; init; }
    public IReadOnlyList<string> SkillsUsed { get; init; } = Array.Empty<string>();

    /// <summary>Estimated cost in USD, computed from token counts via
    /// Services/PriceTable.cs. Not the authoritative billed amount.</summary>
    public decimal EstimatedCostUsd { get; set; }

    public GitInfo? Git { get; set; }

    public TimeSpan Elapsed => LastActivityAt - StartedAt;
}

/// <summary>Git state for a session's working directory.</summary>
public sealed record GitInfo
{
    public string? Branch { get; init; }
    public string? Sha { get; init; }
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public int StagedCount { get; init; }
    public int UnstagedCount { get; init; }
    public int UntrackedCount { get; init; }
    public int ConflictCount { get; init; }
    public int Insertions { get; init; }
    public int Deletions { get; init; }
    public bool IsClean => StagedCount == 0 && UnstagedCount == 0 && UntrackedCount == 0;
    public string? RootDirectoryName { get; init; }
    public string? OriginOwner { get; init; }
    public string? OriginRepo { get; init; }
    public string? UpstreamOwner { get; init; }
    public string? UpstreamRepo { get; init; }
    public bool IsFork { get; init; }
    public string? WorktreeName { get; init; }

    /// <summary>Populated only if `gh` is installed and authenticated.</summary>
    public string? PullRequestUrl { get; init; }
    public string? PullRequestStatus { get; init; }
    public string? CiStatus { get; init; }
}
