using StickyAIusage.Models;

namespace StickyAIusage.ViewModels;

/// <summary>One row of chips on a card, so the templates can nest an
/// ItemsControl per row instead of hard-coding three of them.</summary>
public sealed record SegmentRow(IReadOnlyList<Segment> Segments);

/// <summary>One "Label: Value" block in a card's colored segment row. Its
/// position in the row picks its color and whether it has a left notch -
/// carried as data so the template doesn't have to find the item's index in
/// the visual tree (that approach silently left every segment the first
/// color). There are as many colors as a row can hold fields, so a row never
/// repeats one: rose, sand, steel, sage, sky, violet, blush, lime.</summary>
public sealed record Segment(string Label, string Value, int Position = 0)
{
    /// <summary>Colors in the cycle - one per slot in a full row.</summary>
    public const int Colors = 8;

    public int ColorIndex => Position % Colors;
    public bool IsFirst => Position == 0;
}

public enum ContextLevel { Ok, Warn, High }

/// <summary>
/// Display model for one session card in the expanded panel. Which fields
/// appear, and how they're grouped into colored chip rows, comes from the
/// user's CardLayout; SessionFields holds the catalog (model, effort, tokens,
/// cache, context, git and the rest).
/// Deliberately plain .NET with precomputed strings: the XAML only binds, and
/// all formatting is unit-tested.
/// </summary>
public sealed class SessionCardViewModel
{
    /// <summary>A session whose transcript changed this recently is marked "Live".</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(2);

    public required string SessionId { get; init; }
    public required string ShortId { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required decimal CostUsd { get; init; }
    public required string CostText { get; init; }
    public required bool IsLive { get; init; }

    /// <summary>The chip rows, in the order the user arranged them
    /// (CardLayout). Fields with nothing to show are left out, and a row
    /// whose fields all had nothing to show doesn't appear at all.</summary>
    public required IReadOnlyList<SegmentRow> Rows { get; init; }

    public required bool HasContext { get; init; }
    public required double ContextPercent { get; init; }
    public required string ContextText { get; init; }
    public required ContextLevel ContextLevel { get; init; }

    public required bool HasGit { get; init; }
    public string? GitBranch { get; init; }
    public string? GitStateText { get; init; }
    public required bool HasGitChanges { get; init; }
    public string? GitAddedText { get; init; }
    public string? GitRemovedText { get; init; }
    public string? GitSyncText { get; init; }
    public string? GitRepoText { get; init; }
    public string? GitPrText { get; init; }

    public required bool HasFooter { get; init; }
    public required string FooterText { get; init; }

    /// <summary>Everything displayed, concatenated - lets the panel replace a
    /// card only when what it shows actually changed, instead of re-creating
    /// every card (and losing scroll position) on each poll.</summary>
    public required string Signature { get; init; }

    public static SessionCardViewModel From(SessionInfo s, DateTimeOffset now, CardLayout? layout = null)
    {
        layout ??= CardLayout.Default;
        var shortId = s.SessionId.Length > 8 ? s.SessionId[..8] : s.SessionId;
        // Split on both separators explicitly: transcripts record Windows paths,
        // and Path.GetFileName only knows the current OS's separator.
        var folder = s.WorkingDirectory.TrimEnd('/', '\\').Split('/', '\\').Last();
        if (string.IsNullOrEmpty(folder)) folder = s.WorkingDirectory;
        var project = Format.ProjectName(folder);
        var title = string.IsNullOrWhiteSpace(s.SessionName) ? project : s.SessionName!;

        var subtitle = string.Join(" · ", new[]
        {
            title == project ? null : project,
            $"#{shortId}",
            $"ran {Format.Duration(s.Elapsed)}",
            $"last active {Format.Ago(now - s.LastActivityAt)}"
        }.Where(p => p is not null));

        var rows = new List<SegmentRow>();
        foreach (var row in layout.Rows)
        {
            var segments = new List<Segment>();
            foreach (var id in row)
            {
                if (SessionFields.Find(id) is not { } field || field.Value(s) is not { } value)
                    continue;
                segments.Add(new Segment(field.Name, value, segments.Count));
            }
            if (segments.Count > 0)
                rows.Add(new SegmentRow(segments));
        }

        var hasContext = layout.ShowContextBar && s.ContextWindowSize is > 0;
        var contextPercent = hasContext ? 100.0 * s.CurrentContextTokens / s.ContextWindowSize!.Value : 0;
        var contextText = hasContext
            ? $"{Format.Compact(s.CurrentContextTokens)} / {Format.Compact(s.ContextWindowSize!.Value)} · {Format.Percent(contextPercent)}"
            : "";

        var g = layout.ShowGitDetails ? s.Git : null;
        string? gitState = null, added = null, removed = null, sync = null, repo = null, pr = null;
        if (g is not null)
        {
            var parts = new List<string>();
            if (g.StagedCount > 0) parts.Add($"{g.StagedCount} staged");
            if (g.UnstagedCount > 0) parts.Add($"{g.UnstagedCount} modified");
            if (g.UntrackedCount > 0) parts.Add($"{g.UntrackedCount} untracked");
            if (g.ConflictCount > 0) parts.Add($"{g.ConflictCount} conflict{(g.ConflictCount == 1 ? "" : "s")}");
            gitState = parts.Count == 0 ? "clean" : string.Join(" · ", parts);

            if (g.Insertions > 0 || g.Deletions > 0)
            {
                added = $"+{g.Insertions}";
                removed = $"−{g.Deletions}";
            }
            if (g.AheadCount > 0 || g.BehindCount > 0)
                sync = $"↑{g.AheadCount} ↓{g.BehindCount}";
            if (g.OriginOwner is not null)
                repo = $"{g.OriginOwner}/{g.OriginRepo}{(g.IsFork ? " (fork)" : "")}";
            if (g.PullRequestStatus is not null)
                pr = $"PR {g.PullRequestStatus.ToLowerInvariant()}" +
                     (g.CiStatus is null ? "" : $" · CI {g.CiStatus.ToLowerInvariant()}");
        }

        var footerParts = new List<string>();
        if (s.SkillsUsed.Count > 0) footerParts.Add("Skills: " + string.Join(", ", s.SkillsUsed));
        if (!string.IsNullOrEmpty(s.ClaudeCodeVersion)) footerParts.Add("Claude Code " + s.ClaudeCodeVersion);
        var footer = string.Join("  ·  ", footerParts);

        var isLive = now - s.LastActivityAt < LiveWindow;
        var costText = Format.Usd(s.EstimatedCostUsd);
        var signature = string.Join("|", new object?[]
        {
            title, subtitle, costText, isLive, contextText, g?.Branch, gitState, added, removed, sync, repo, pr, footer,
            string.Join(";", rows.Select(r => string.Join(",", r.Segments)))
        });

        return new SessionCardViewModel
        {
            SessionId = s.SessionId,
            ShortId = shortId,
            Title = title,
            Subtitle = subtitle,
            CostUsd = s.EstimatedCostUsd,
            CostText = costText,
            IsLive = isLive,
            Rows = rows,
            HasContext = hasContext,
            ContextPercent = Math.Min(100, contextPercent),
            ContextText = contextText,
            ContextLevel = contextPercent >= 85 ? ContextLevel.High : contextPercent >= 60 ? ContextLevel.Warn : ContextLevel.Ok,
            HasGit = g is not null,
            GitBranch = g?.Branch,
            GitStateText = gitState,
            HasGitChanges = added is not null,
            GitAddedText = added,
            GitRemovedText = removed,
            GitSyncText = sync,
            GitRepoText = repo,
            GitPrText = pr,
            HasFooter = footer.Length > 0,
            FooterText = footer,
            Signature = signature
        };
    }

}
