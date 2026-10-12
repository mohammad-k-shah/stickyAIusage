using StickyAIusage.Models;

namespace StickyAIusage.ViewModels;

/// <summary>
/// A made-up session for the field customizer's live preview, so the preview
/// is the real card with real formatting rather than a drawing of one. Every
/// field in the catalog has something to show here - otherwise a field could
/// be added and appear to do nothing.
/// </summary>
public static class CardPreview
{
    public static SessionInfo Sample(DateTimeOffset now) => new()
    {
        SessionId = "3c8e62c4-17a7-453e-8385-88147c5aaf07",
        SessionName = "Refactor payment retries",
        TranscriptPath = "",
        WorkingDirectory = "/Users/you/dev/AcmeWebApp",
        Model = "claude-opus-5",
        ThinkingEffort = "high",
        ClaudeCodeVersion = "2.1.275",
        StartedAt = now.AddMinutes(-37),
        LastActivityAt = now.AddSeconds(-25),
        InputTokens = 1_240,
        OutputTokens = 48_310,
        CacheReadTokens = 3_300_821,
        CacheCreationTokens = 469_573,
        ContextWindowSize = 1_000_000,
        CurrentContextTokens = 247_309,
        CompactionCount = 2,
        SkillsUsed = ["anthropic-skills:docx"],
        EstimatedCostUsd = 3.05m,
        Git = new GitInfo
        {
            Branch = "feature/checkout-redesign",
            Sha = "9f2c41ab3d7e",
            StagedCount = 2,
            UnstagedCount = 1,
            UntrackedCount = 4,
            Insertions = 274,
            Deletions = 39,
            AheadCount = 1,
            BehindCount = 2,
            ConflictCount = 1,
            RootDirectoryName = "acme-webapp",
            OriginOwner = "acme",
            OriginRepo = "acme-webapp",
            UpstreamOwner = "acme-labs",
            UpstreamRepo = "acme-webapp",
            IsFork = true,
            WorktreeName = "checkout",
            PullRequestStatus = "OPEN",
            CiStatus = "SUCCESS"
        }
    };

    /// <summary>The preview card for a layout being edited.</summary>
    public static SessionCardViewModel Card(CardLayout layout, DateTimeOffset now) =>
        SessionCardViewModel.From(Sample(now), now, layout);
}
