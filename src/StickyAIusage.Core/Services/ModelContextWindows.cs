namespace StickyAIusage.Services;

/// <summary>Context-window sizes by model id, for the Context Window /
/// Context % / Usable % fields, from the price list (<see cref="ModelCatalog"/>).
/// A model the list doesn't know gets its family's newest size, or the
/// fallback row's 200K. No model id → null, so the UI hides those fields.</summary>
public static class ModelContextWindows
{
    public static long? Lookup(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? null : ModelCatalog.Current.Find(modelId).Row.ContextWindow;

    /// <summary>Auto-compact kicks in before the hard limit, so 80% of the
    /// maximum is the part of the window you can actually use.</summary>
    public const double UsableFraction = 0.80;
}
