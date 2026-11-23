namespace StickyAIusage.Forecasting;

/// <summary>One reading of one usage window: how much of it was used at a
/// moment. <paramref name="ResetsAt"/> names the window instance: a changed
/// reset time is a new window, and its samples form a new series.</summary>
public sealed record UsageSample(
    string ProviderId,
    string Kind,
    DateTimeOffset? ResetsAt,
    double PercentUsed,
    DateTimeOffset ObservedAt);
