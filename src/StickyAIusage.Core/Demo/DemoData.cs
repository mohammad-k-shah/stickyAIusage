using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Demo;

/// <summary>Which account the demo plays: Pro/Max limits, or an Enterprise-style monthly cap.</summary>
public enum DemoVariant
{
    Plan,
    Cap
}

/// <summary>
/// The demo's scripted two minutes (<c>--demo</c>, the README pictures, the self-test): a 5-hour
/// window climbing from 62% to 91% with a pace forecast that turns yellow, orange and red, three
/// Claude Code sessions (working → needs you → done), a Codex card, and with
/// <see cref="DemoVariant.Cap"/> a monthly cap instead of the windows. Everything is worked out
/// from the start time and the clock, so the same two instants always give the same screens.
/// Every name in it is made up (acme-webapp, payments-api, docs-site); nothing comes from this
/// computer.
/// </summary>
public sealed class DemoData
{
    /// <summary>How long the climb takes; after it the numbers hold.</summary>
    public static readonly TimeSpan Climb = TimeSpan.FromMinutes(2);

    public const double FiveHourFrom = 62;
    public const double FiveHourTo = 91;

    /// <summary>The 5-hour window resets this long after the demo starts.</summary>
    public static readonly TimeSpan FiveHourResetsIn = new(2, 5, 0);

    /// <summary>When the forecast limit comes, after the start, by stage: the chip is yellow, then
    /// orange (within the hour), then red and close enough for the "limit at" alert (within 30 minutes).</summary>
    public static readonly TimeSpan CalmLimitIn = TimeSpan.FromMinutes(75);
    public static readonly TimeSpan BusyLimitIn = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan UrgentLimitIn = TimeSpan.FromMinutes(26);

    /// <summary>The percentages at which the forecast moves to the next stage.</summary>
    public const double BusyFrom = 75;
    public const double UrgentFrom = 88;

    /// <summary>When the first session starts waiting for a permission, and when the third is done.</summary>
    public static readonly TimeSpan NeedsYouAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DoneAfter = TimeSpan.FromSeconds(40);

    public const decimal CapLimitUsd = 200m;
    public const decimal CapFromUsd = 152.40m;
    public const decimal CapToUsd = 186.90m;

    public const string PluginVersion = "2.3.0";
    public const string ClaudeCodeVersion = "2.1.325";

    /// <summary>The three sessions' ids (the feed records and the cards match on them).</summary>
    public const string CheckoutSession = "3c8e62c4-17a7-453e-8385-88147c5aaf07";
    public const string TestsSession = "7481c4b9-6458-421f-b183-b35bb06d1e5d";
    public const string DocsSession = "9e1f0a3b-55c2-4d0e-b7a1-2c6d8e4f9a10";

    public DemoData(DemoVariant variant, DateTimeOffset start)
    {
        Variant = variant;
        Start = start;
    }

    public DemoVariant Variant { get; }

    public DateTimeOffset Start { get; }

    /// <summary>A demo that is <paramref name="elapsed"/> into its script at <paramref name="now"/>.</summary>
    public static DemoData At(DemoVariant variant, DateTimeOffset now, TimeSpan elapsed) => new(variant, now - elapsed);

    /// <summary>0 at the start, 1 once the climb is over (and before the start).</summary>
    public double Progress(DateTimeOffset now) =>
        Math.Clamp((now - Start).TotalSeconds / Climb.TotalSeconds, 0, 1);

    /// <summary>The 5-hour window's percentage: whole points, as Claude Code reports them.</summary>
    public double FiveHourPercent(DateTimeOffset now) =>
        Math.Round(FiveHourFrom + (FiveHourTo - FiveHourFrom) * Progress(now), MidpointRounding.AwayFromZero);

    public double WeeklyPercent(DateTimeOffset now) => Math.Round(34 + 2 * Progress(now), MidpointRounding.AwayFromZero);

    public decimal CapSpentUsd(DateTimeOffset now) =>
        Math.Round(CapFromUsd + (CapToUsd - CapFromUsd) * (decimal)Progress(now), 2, MidpointRounding.AwayFromZero);

    public DateTimeOffset FiveHourResetsAt => Start + FiveHourResetsIn;

    public DateTimeOffset WeeklyResetsAt => Start + new TimeSpan(3, 4, 0, 0);

    public DateTimeOffset CapResetsAt => Start + TimeSpan.FromDays(9);

    /// <summary>The reading the widget shows, as the plugin's feed would give it.</summary>
    public UsageSnapshot Usage(DateTimeOffset now) => Variant == DemoVariant.Cap
        ? new UsageSnapshot
        {
            Mode = UsageMode.MonthlySpend,
            HasMonthlySpend = true,
            MonthlySpendUsedUsd = CapSpentUsd(now),
            MonthlySpendLimitUsd = CapLimitUsd,
            MonthlySpendPercent = (double)Math.Round(CapSpentUsd(now) / CapLimitUsd * 100, 1, MidpointRounding.AwayFromZero),
            MonthlySpendResetsAt = CapResetsAt,
            FetchedAt = now,
            SourceId = ClaudeFeedUsageSource.Id,
            Trust = SourceTrust.Official
        }
        : new UsageSnapshot
        {
            Mode = UsageMode.PlanLimits,
            SubscriptionType = "max",
            FiveHourUtilizationPercent = FiveHourPercent(now),
            FiveHourResetsAt = FiveHourResetsAt,
            WeeklyUtilizationPercent = WeeklyPercent(now),
            WeeklyResetsAt = WeeklyResetsAt,
            FetchedAt = now,
            SourceId = ClaudeFeedUsageSource.Id,
            Trust = SourceTrust.Official
        };

    /// <summary>When the 5-hour limit comes at the pace of the moment (one of three stages).</summary>
    public DateTimeOffset FiveHourLimitAt(DateTimeOffset now)
    {
        var percent = FiveHourPercent(now);
        return Start + (percent >= UrgentFrom ? UrgentLimitIn : percent >= BusyFrom ? BusyLimitIn : CalmLimitIn);
    }

    /// <summary>The pace of each window, as the forecaster would say it after half an hour of this.</summary>
    public IReadOnlyList<PaceForecast> Forecasts(DateTimeOffset now)
    {
        if (Variant == DemoVariant.Cap)
        {
            var spent = (double)(CapSpentUsd(now) / CapLimitUsd * 100);
            return
            [
                new PaceForecast(UsageWindowKinds.MonthSpend, Math.Round(spent, 1), 0.6, null, 112, 9,
                    ForecastConfidence.High, "the last 7 days") { ResetsAt = CapResetsAt, IsActive = true }
            ];
        }

        var fiveHour = FiveHourPercent(now);
        var limitAt = FiveHourLimitAt(now);
        var hoursLeft = Math.Max((limitAt - now).TotalHours, 0.05);
        var rate = Math.Round((100 - fiveHour) / hoursLeft, 1);
        var projected = Math.Round(fiveHour + rate * (FiveHourResetsAt - now).TotalHours);
        // The window started five hours before its reset: an even pace would be at this share of it.
        var even = (TimeSpan.FromHours(5) - (FiveHourResetsAt - now)).TotalHours / 5 * 100;
        return
        [
            new PaceForecast(UsageWindowKinds.FiveHour, fiveHour, rate, limitAt, projected, Math.Round(fiveHour - even),
                ForecastConfidence.High, "the last 30 minutes") { ResetsAt = FiveHourResetsAt, IsActive = true },
            new PaceForecast(UsageWindowKinds.SevenDay, WeeklyPercent(now), 0.4, null, 62, 3,
                ForecastConfidence.Low, "the last 24 hours") { ResetsAt = WeeklyResetsAt, IsActive = true }
        ];
    }

    /// <summary>The three sessions as the plugin's records describe them: the lights.</summary>
    public IReadOnlyList<FeedSession> Records(DateTimeOffset now)
    {
        var elapsed = now - Start;
        var waiting = elapsed >= NeedsYouAfter;
        var docsDone = elapsed >= DoneAfter;
        return
        [
            Record(CheckoutSession, CheckoutFolder, "claude-opus-5", now, TimeSpan.FromMinutes(37),
                waiting ? "needs_you" : "working", waiting ? Start + NeedsYouAfter : Start - TimeSpan.FromMinutes(4),
                waiting ? new FeedAttention("permission", "Bash") : null),
            Record(TestsSession, Folder("payments-api"), "claude-sonnet-5-5", now, TimeSpan.FromMinutes(19), "working",
                Start - TimeSpan.FromMinutes(6), null),
            Record(DocsSession, Folder("docs-site"), "claude-haiku-5-5", now, TimeSpan.FromMinutes(12),
                docsDone ? "done" : "working", docsDone ? Start + DoneAfter : Start - TimeSpan.FromMinutes(2), null,
                docsDone ? TimeSpan.FromMinutes(12) : null)
        ];
    }

    private static FeedSession Record(string id, string folder, string model, DateTimeOffset now, TimeSpan age,
        string state, DateTimeOffset since, FeedAttention? attention, TimeSpan? lastTurn = null) =>
        new(id, "terminal", folder, model, ClaudeCodeVersion, PluginVersion, now, now - age, null, null,
            null, null, null, null, state, since, attention, lastTurn);

    /// <summary>The first session's folder (the customizer's sample session).</summary>
    public static readonly string CheckoutFolder = CardPreview.Sample(DateTimeOffset.UnixEpoch).WorkingDirectory;

    /// <summary>Where Claude Code would keep the session's transcript (no file is read: the demo has none).</summary>
    private static string Transcript(string project, string id) => $"/Users/you/.claude/projects/-Users-you-dev-{project}/{id}.jsonl";

    /// <summary>A made-up folder: the same on every computer.</summary>
    public static string Folder(string project) => "/Users/you/dev/" + project;

    /// <summary>The flyout's three session cards (the first is the customizer's sample).</summary>
    public IReadOnlyList<SessionInfo> Sessions(DateTimeOffset now)
    {
        var grow = Progress(now);
        var checkout = CardPreview.Sample(now);
        return
        [
            checkout,
            new SessionInfo
            {
                SessionId = TestsSession,
                SessionName = "Write API integration tests",
                TranscriptPath = Transcript("payments-api", TestsSession),
                WorkingDirectory = Folder("payments-api"),
                Model = "claude-sonnet-5-5",
                ThinkingEffort = "medium",
                ClaudeCodeVersion = ClaudeCodeVersion,
                StartedAt = now.AddMinutes(-19),
                LastActivityAt = now.AddSeconds(-4),
                InputTokens = 820,
                OutputTokens = 21_400 + (long)(6_000 * grow),
                CacheReadTokens = 1_412_009,
                CacheCreationTokens = 188_230,
                ContextWindowSize = 1_000_000,
                CurrentContextTokens = 106_495 + (long)(30_000 * grow),
                EstimatedCostUsd = Math.Round(0.84m + 0.30m * (decimal)grow, 2),
                SkillsUsed = []
            },
            new SessionInfo
            {
                SessionId = DocsSession,
                SessionName = "Update the onboarding guide",
                TranscriptPath = Transcript("docs-site", DocsSession),
                WorkingDirectory = Folder("docs-site"),
                Model = "claude-haiku-5-5",
                ClaudeCodeVersion = ClaudeCodeVersion,
                StartedAt = now.AddMinutes(-14),
                LastActivityAt = now - Start >= DoneAfter ? Start + DoneAfter : now.AddSeconds(-6),
                InputTokens = 310,
                OutputTokens = 9_870,
                CacheReadTokens = 402_116,
                CacheCreationTokens = 61_004,
                ContextWindowSize = 200_000,
                CurrentContextTokens = 48_200,
                EstimatedCostUsd = 0.12m,
                SkillsUsed = []
            }
        ];
    }

    /// <summary>The cards' made-up repositories, by folder.</summary>
    public GitInfo? Git(string workingDirectory) => workingDirectory switch
    {
        _ when workingDirectory == CheckoutFolder => CardPreview.Sample(Start).Git,
        _ when workingDirectory == Folder("payments-api") => new GitInfo
        {
            Branch = "test/refund-webhooks", Sha = "41d0e9c27b55", StagedCount = 0, UnstagedCount = 3, UntrackedCount = 2,
            Insertions = 186, Deletions = 12, RootDirectoryName = "payments-api", OriginOwner = "acme", OriginRepo = "payments-api",
            PullRequestStatus = "OPEN", CiStatus = "PENDING"
        },
        _ when workingDirectory == Folder("docs-site") => new GitInfo
        {
            Branch = "docs/onboarding", Sha = "a7b3f0e19c42", AheadCount = 2, RootDirectoryName = "docs-site",
            OriginOwner = "acme", OriginRepo = "docs-site"
        },
        _ => null
    };

    /// <summary>Codex beside Claude: a quieter 5-hour window and its week.</summary>
    public ProviderUsage Codex(DateTimeOffset now) =>
        new(CodexUsageProvider.Id, "Codex", "Plus",
            [
                new UsageWindow(UsageWindowKinds.FiveHour, Math.Round(41 + 6 * Progress(now)), Start + new TimeSpan(3, 20, 0),
                    TimeSpan.FromHours(5)),
                new UsageWindow(UsageWindowKinds.SevenDay, 18, Start + new TimeSpan(5, 2, 0, 0), TimeSpan.FromDays(7))
            ], null, now, CodexUsageProvider.SourceIdValue, SourceTrust.Derived, UsageHealth.Ok, null)
        {
            WholePointsOnly = true,
            SourceLabel = CodexUsageProvider.SourceLabelText,
            WindowLabels = new Dictionary<string, string> { [UsageWindowKinds.FiveHour] = "5-hour window", [UsageWindowKinds.SevenDay] = "Weekly" }
        };

    /// <summary>Every text the demo can put on screen, for the "nothing real" check.</summary>
    public IEnumerable<string> AllText(DateTimeOffset now)
    {
        foreach (var session in Sessions(now))
        {
            yield return session.SessionName ?? "";
            yield return session.WorkingDirectory;
            if (Git(session.WorkingDirectory) is { } git)
            {
                yield return git.Branch ?? "";
                yield return git.OriginOwner ?? "";
                yield return git.OriginRepo ?? "";
                yield return git.UpstreamOwner ?? "";
                yield return git.WorktreeName ?? "";
            }
        }
        foreach (var record in Records(now))
            yield return record.Cwd ?? "";
    }
}
