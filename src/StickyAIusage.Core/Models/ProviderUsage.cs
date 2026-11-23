namespace StickyAIusage.Models;

/// <summary>
/// One provider's account usage in a shape that doesn't depend on the
/// provider: a list of windows, an optional spend cap, and where the numbers
/// came from. The Claude layouts keep reading <see cref="UsageSnapshot"/>;
/// features that work for any provider (forecasts, alerts, the status file,
/// other tools) read this.
/// </summary>
public sealed record ProviderUsage(
    string ProviderId,
    string DisplayName,
    string? PlanName,
    IReadOnlyList<UsageWindow> Windows,
    SpendCap? Spend,
    DateTimeOffset ObservedAt,
    string SourceId,
    SourceTrust Trust,
    UsageHealth Health,
    string? HealthDetail)
{
    /// <summary>The window of that kind, or null when the provider didn't report it.</summary>
    public UsageWindow? Window(string kind) => Windows.FirstOrDefault(w => w.Kind == kind);

    /// <summary>The provider reports whole percentage points only (Codex): the
    /// pace needs a rise of two points, not one, before it calls a window active.</summary>
    public bool WholePointsOnly { get; init; }

    /// <summary>The flyout's line under the section title, saying where the numbers
    /// come from ("from Codex's local session log"); null: none.</summary>
    public string? SourceLabel { get; init; }

    /// <summary>The window's name in the flyout and in alerts, when the provider
    /// knows better than its kind ("5-hour window", "Daily"); null: from the kind.</summary>
    public IReadOnlyDictionary<string, string> WindowLabels { get; init; } = new Dictionary<string, string>();
}
