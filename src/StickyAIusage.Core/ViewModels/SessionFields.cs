using StickyAIusage.Models;

namespace StickyAIusage.ViewModels;

/// <summary>Groups in the field picker, in the order they're listed - the
/// order a status line usually reads in: what you're running, then the repo,
/// the environment, and the numbers behind the session.</summary>
public enum FieldCategory { Core, Git, Environment, Tokens, Cache, Context, Session }

/// <summary>
/// One value a session card can show, as a labelled chip ("Model: Opus 5").
/// Everything a terminal status line can show about a session, minus the
/// values that only mean something inside a terminal (vim mode, terminal
/// width) and the ones about the account rather than the session (weekly
/// usage, block timers) - those belong to the pill and the usage card.
///
/// <see cref="Value"/> returns null when the session has nothing to show, and
/// the chip is left out rather than rendered empty.
/// </summary>
public sealed record SessionField(
    string Id,
    string Name,
    FieldCategory Category,
    string Description,
    Func<SessionInfo, string?> Value);

public static class SessionFields
{
    /// <summary>Every field, in picker order. The id is what's persisted, so
    /// don't rename one without leaving the old id working.</summary>
    public static IReadOnlyList<SessionField> All { get; } = Build();

    private static readonly Dictionary<string, SessionField> ById =
        All.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    public static SessionField? Find(string id) => ById.GetValueOrDefault(id);

    public static IEnumerable<SessionField> InCategory(FieldCategory category) =>
        All.Where(f => f.Category == category);

    public static string CategoryName(FieldCategory category) => category.ToString();

    private static List<SessionField> Build()
    {
        var fields = new List<SessionField>();

        void Add(string id, string name, FieldCategory category, string description, Func<SessionInfo, string?> value) =>
            fields.Add(new SessionField(id, name, category, description, value));

        // ------------------------------- core, session and environment
        Add("model", "Model", FieldCategory.Core, "The model the session is on now",
            s => Format.ModelName(s.Model));
        Add("effort", "Effort", FieldCategory.Core, "Thinking effort: Low, Medium, High, Extra high, Max or Ultracode",
            s => Format.Effort(s.ThinkingEffort));
        Add("cost", "Cost", FieldCategory.Session, "Estimated cost of the session so far",
            s => Format.Usd(s.EstimatedCostUsd));
        Add("session-name", "Session name", FieldCategory.Session, "The name Claude Code gave the conversation",
            s => Blank(s.SessionName));
        Add("session-id", "Session ID", FieldCategory.Core, "First 8 characters of the session's id",
            s => s.SessionId.Length > 8 ? s.SessionId[..8] : s.SessionId);
        Add("project", "Project", FieldCategory.Environment, "The working folder's name, spaced out",
            s => Blank(Format.ProjectName(FolderName(s))));
        Add("cwd", "Working folder", FieldCategory.Environment, "Full path of the session's working folder",
            s => Blank(s.WorkingDirectory));
        Add("elapsed", "Ran for", FieldCategory.Session, "Time from the session's first message to its last",
            s => Format.Duration(s.Elapsed));
        Add("last-active", "Last active", FieldCategory.Session, "How long ago the session last did anything",
            s => Format.Ago(DateTimeOffset.Now - s.LastActivityAt));
        Add("started", "Started", FieldCategory.Session, "Local time of the session's first message",
            s => s.StartedAt.ToLocalTime().ToString("h:mm tt"));
        Add("compactions", "Compactions", FieldCategory.Context, "How many times the conversation was compacted",
            s => s.CompactionCount > 0 ? s.CompactionCount.ToString() : null);
        Add("skills", "Skills", FieldCategory.Session, "Skills used in the session",
            s => s.SkillsUsed.Count > 0 ? string.Join(", ", s.SkillsUsed) : null);
        Add("version", "Claude Code", FieldCategory.Core, "The Claude Code version that wrote the transcript",
            s => Blank(s.ClaudeCodeVersion));

        // -------------------------------------------------------- tokens
        Add("in", "In", FieldCategory.Tokens, "Input tokens (not counting cache)",
            s => Format.Compact(s.InputTokens));
        Add("out", "Out", FieldCategory.Tokens, "Output tokens",
            s => Format.Compact(s.OutputTokens));
        Add("cached", "Cached", FieldCategory.Tokens, "Cache reads plus cache writes",
            s => Format.Compact(s.CacheReadTokens + s.CacheCreationTokens));
        Add("total", "Total", FieldCategory.Tokens, "Every token the session has used",
            s => Format.Compact(s.TotalTokens));

        // --------------------------------------------------------- cache
        Add("cache-hit", "Cache Hit", FieldCategory.Cache, "Share of cached tokens that were read rather than written",
            s => Cached(s) > 0 ? Format.Percent(100.0 * s.CacheReadTokens / Cached(s)) : null);
        Add("cache-read", "Cache Read", FieldCategory.Cache, "Tokens read from the prompt cache, and their share of the total",
            s => WithShare(s.CacheReadTokens, s.TotalTokens));
        Add("cache-write", "Cache Write", FieldCategory.Cache, "Tokens written to the prompt cache, and their share of the total",
            s => WithShare(s.CacheCreationTokens, s.TotalTokens));

        // ------------------------------------------------------- context
        Add("context", "Context", FieldCategory.Context, "Context in use out of the model's window, with a percentage",
            s => s.ContextWindowSize is > 0
                ? $"{Format.Compact(s.CurrentContextTokens)} / {Format.Compact(s.ContextWindowSize.Value)} · {Format.Percent(ContextPercent(s))}"
                : null);
        Add("context-percent", "Context %", FieldCategory.Context, "Percentage of the context window in use",
            s => s.ContextWindowSize is > 0 ? Format.Percent(ContextPercent(s)) : null);
        Add("context-length", "Context length", FieldCategory.Context, "Tokens currently in context",
            s => Format.Compact(s.CurrentContextTokens));
        Add("context-window", "Context window", FieldCategory.Context, "The model's maximum context",
            s => s.ContextWindowSize is > 0 ? Format.Compact(s.ContextWindowSize.Value) : null);

        // ----------------------------------------------------------- git
        Add("git-branch", "Branch", FieldCategory.Git, "Current branch",
            s => Blank(s.Git?.Branch));
        Add("git-sha", "Commit", FieldCategory.Git, "Short commit hash of HEAD",
            s => s.Git?.Sha is { Length: > 0 } sha ? (sha.Length > 7 ? sha[..7] : sha) : null);
        Add("git-status", "Changes", FieldCategory.Git, "Staged, modified and untracked counts, or \"clean\"",
            s => s.Git is null ? null : GitStatus(s.Git));
        Add("git-clean", "Clean", FieldCategory.Git, "Whether the working tree has no changes",
            s => s.Git is null ? null : s.Git.IsClean ? "yes" : "no");
        Add("git-staged", "Staged", FieldCategory.Git, "Number of staged files",
            s => Count(s.Git?.StagedCount));
        Add("git-unstaged", "Modified", FieldCategory.Git, "Number of modified, unstaged files",
            s => Count(s.Git?.UnstagedCount));
        Add("git-untracked", "Untracked", FieldCategory.Git, "Number of untracked files",
            s => Count(s.Git?.UntrackedCount));
        Add("git-conflicts", "Conflicts", FieldCategory.Git, "Number of conflicted files",
            s => Count(s.Git?.ConflictCount));
        Add("git-insertions", "Added lines", FieldCategory.Git, "Lines added in the working tree",
            s => Count(s.Git?.Insertions) is { } v ? "+" + v : null);
        Add("git-deletions", "Removed lines", FieldCategory.Git, "Lines removed in the working tree",
            s => Count(s.Git?.Deletions) is { } v ? "−" + v : null);
        Add("git-diff", "Diff", FieldCategory.Git, "Added and removed lines together",
            s => s.Git is { } g && (g.Insertions > 0 || g.Deletions > 0) ? $"+{g.Insertions} −{g.Deletions}" : null);
        Add("git-ahead-behind", "Ahead/behind", FieldCategory.Git, "Commits ahead of and behind the upstream branch",
            s => s.Git is { } g && (g.AheadCount > 0 || g.BehindCount > 0) ? $"↑{g.AheadCount} ↓{g.BehindCount}" : null);
        Add("git-ahead", "Ahead", FieldCategory.Git, "Commits not yet pushed",
            s => Count(s.Git?.AheadCount));
        Add("git-behind", "Behind", FieldCategory.Git, "Commits not yet pulled",
            s => Count(s.Git?.BehindCount));
        Add("git-repo", "Repository", FieldCategory.Git, "owner/repo of the origin remote",
            s => s.Git?.OriginOwner is null ? null : $"{s.Git.OriginOwner}/{s.Git.OriginRepo}");
        Add("git-origin-owner", "Origin owner", FieldCategory.Git, "Owner of the origin remote",
            s => Blank(s.Git?.OriginOwner));
        Add("git-origin-repo", "Origin repo", FieldCategory.Git, "Repository name of the origin remote",
            s => Blank(s.Git?.OriginRepo));
        Add("git-upstream", "Upstream", FieldCategory.Git, "owner/repo of the upstream remote (forks)",
            s => s.Git?.UpstreamOwner is null ? null : $"{s.Git.UpstreamOwner}/{s.Git.UpstreamRepo}");
        Add("git-upstream-owner", "Upstream owner", FieldCategory.Git, "Owner of the upstream remote",
            s => Blank(s.Git?.UpstreamOwner));
        Add("git-upstream-repo", "Upstream repo", FieldCategory.Git, "Repository name of the upstream remote",
            s => Blank(s.Git?.UpstreamRepo));
        Add("git-fork", "Fork", FieldCategory.Git, "Whether the repository is a fork",
            s => s.Git is null ? null : s.Git.IsFork ? "yes" : null);
        Add("git-root", "Repo folder", FieldCategory.Git, "Name of the repository's root folder",
            s => Blank(s.Git?.RootDirectoryName));
        Add("git-worktree", "Worktree", FieldCategory.Git, "Name of the git worktree, when not the main one",
            s => Blank(s.Git?.WorktreeName));
        Add("git-pr", "PR", FieldCategory.Git, "Pull request state, via the gh CLI",
            s => s.Git?.PullRequestStatus is { } pr ? pr.ToLowerInvariant() : null);
        Add("git-ci", "CI", FieldCategory.Git, "CI state of the pull request, via the gh CLI",
            s => s.Git?.CiStatus is { } ci ? ci.ToLowerInvariant() : null);

        return fields;
    }

    private static long Cached(SessionInfo s) => s.CacheReadTokens + s.CacheCreationTokens;

    private static double ContextPercent(SessionInfo s) =>
        s.ContextWindowSize is > 0 ? 100.0 * s.CurrentContextTokens / s.ContextWindowSize.Value : 0;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Count(int? value) => value is > 0 ? value.Value.ToString() : null;

    private static string WithShare(long part, long total) =>
        total > 0 ? $"{Format.Compact(part)} ({Format.Percent(100.0 * part / total)})" : Format.Compact(part);

    private static string GitStatus(GitInfo g)
    {
        var parts = new List<string>();
        if (g.StagedCount > 0) parts.Add($"{g.StagedCount} staged");
        if (g.UnstagedCount > 0) parts.Add($"{g.UnstagedCount} modified");
        if (g.UntrackedCount > 0) parts.Add($"{g.UntrackedCount} untracked");
        if (g.ConflictCount > 0) parts.Add($"{g.ConflictCount} conflict{(g.ConflictCount == 1 ? "" : "s")}");
        return parts.Count == 0 ? "clean" : string.Join(" · ", parts);
    }

    /// <summary>The working folder's last segment. Transcripts record Windows
    /// paths too, so split on both separators rather than the current OS's.</summary>
    private static string FolderName(SessionInfo s)
    {
        var folder = s.WorkingDirectory.TrimEnd('/', '\\').Split('/', '\\').Last();
        return folder.Length == 0 ? s.WorkingDirectory : folder;
    }
}
