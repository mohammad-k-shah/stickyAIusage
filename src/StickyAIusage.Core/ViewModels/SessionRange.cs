namespace StickyAIusage.ViewModels;

/// <summary>The flyout's session date range: how many days back it lists,
/// counting today as day 1. Always starts on today.</summary>
public static class SessionRange
{
    public static readonly int[] Options = [1, 2, 7, 14, 30];

    /// <summary>Tab text: "Today", "2d", "1w", "2w", "30d".</summary>
    public static string Tab(int days) => days switch
    {
        1 => "Today",
        7 => "1w",
        14 => "2w",
        _ => $"{days}d"
    };

    /// <summary>Flyout title: "Today's sessions", "2 Days' sessions",
    /// "1 Week's sessions", "2 Weeks' sessions", "30 Days' sessions".</summary>
    public static string Title(int days) => days switch
    {
        1 => "Today's sessions",
        7 => "1 Week's sessions",
        14 => "2 Weeks' sessions",
        _ => $"{days} Days' sessions"
    };

    /// <summary>"today", "the last 2 days", "the last 2 weeks".</summary>
    private static string Span(int days) => days switch
    {
        1 => "today",
        7 => "the last week",
        14 => "the last 2 weeks",
        _ => $"the last {days} days"
    };

    public static string EmptySummary(int days) => days == 1 ? "Nothing yet today" : $"Nothing in {Span(days)}";

    public static string EmptyText(int days) => days == 1
        ? "No Claude Code sessions today. Start one in Claude Desktop's Code tab or the CLI and it will show up here."
        : $"No Claude Code sessions in {Span(days)}.";
}
