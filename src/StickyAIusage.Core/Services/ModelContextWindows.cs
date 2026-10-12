namespace StickyAIusage.Services;

/// <summary>Known context-window sizes by model id, for the Context Window /
/// Context % / Usable % fields. Unknown models
/// return null so the UI can hide those fields rather than show a wrong size.</summary>
public static class ModelContextWindows
{
    private const long OneMillion = 1_000_000L;
    private const long TwoHundredK = 200_000L;

    // Model families with a 1M-token context window, per Anthropic's models
    // overview. Matched as substrings
    // of the transcript's model id, e.g. "claude-sonnet-5". Everything older
    // (Opus 4.5 and earlier, Sonnet 4.5 and earlier, Haiku 4.5, Claude 3.x)
    // has 200K.
    private static readonly string[] OneMillionContextFamilies =
    [
        "fable-5", "mythos-5",
        "opus-5", "opus-4-8", "opus-4-7", "opus-4-6",
        "sonnet-5", "sonnet-4-6"
    ];

    public static long? Lookup(string? modelId)
    {
        var id = modelId?.ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(id)) return null;

        return OneMillionContextFamilies.Any(id.Contains) ? OneMillion : TwoHundredK;
    }

    /// <summary>Auto-compact kicks in before the hard limit, so 80% of the
    /// maximum is the part of the window you can actually use.</summary>
    public const double UsableFraction = 0.80;
}
