using System.Text.RegularExpressions;

namespace StickyAIusage.Services;

/// <summary>
/// USD-per-million-token list prices used to estimate session cost, since
/// session transcripts carry token counts but no cost. A best-effort estimate,
/// not the billed amount (the monthly figure from Anthropic is authoritative).
///
/// Source: https://platform.claude.com/docs/en/about-claude/pricing, checked
/// 2026-09-19, and matching the price tiers in Claude Code 2.1.275's own model
/// catalog (e.g. Sonnet 5 "tier_2_10", Opus 4.5+ "tier_5_25"). Cache writes
/// kept 5 minutes cost 1.25x the input price, those kept 1 hour 2x; cache
/// reads 0.1x (0.025x on Fable/Mythos 5.1). Update when prices change.
/// </summary>
public static class PriceTable
{
    public sealed record ModelPrices(decimal Input, decimal Output, decimal CacheRead)
    {
        public decimal CacheWrite5m => Input * 1.25m;
        public decimal CacheWrite1h => Input * 2m;
    }

    private static readonly ModelPrices Fable51 = new(10m, 50m, 0.25m);  // also Mythos 5.1
    private static readonly ModelPrices Fable5 = new(10m, 50m, 1m);      // also Mythos 5
    private static readonly ModelPrices OpusCurrent = new(5m, 25m, 0.5m); // Opus 4.5 - 5
    private static readonly ModelPrices OpusLegacy = new(15m, 75m, 1.5m); // Opus 3, 4, 4.1
    private static readonly ModelPrices Sonnet5 = new(2m, 10m, 0.2m);
    private static readonly ModelPrices SonnetLegacy = new(3m, 15m, 0.3m); // Sonnet 3.x - 4.6
    private static readonly ModelPrices Haiku45 = new(1m, 5m, 0.1m);
    private static readonly ModelPrices Haiku35 = new(0.8m, 4m, 0.08m);
    private static readonly ModelPrices Haiku3 = new(0.25m, 1.25m, 0.03m);

    /// <summary>Prices for a model id such as "claude-opus-5",
    /// "claude-sonnet-4-6[1m]" or "claude-3-5-haiku-20241022". Unrecognized
    /// ids get Sonnet 4.6-tier prices.</summary>
    public static ModelPrices For(string? modelId)
    {
        var id = (modelId ?? "").ToLowerInvariant();
        var version = Version(id);

        if (id.Contains("fable") || id.Contains("mythos"))
            return version >= 5.1m ? Fable51 : Fable5;
        if (id.Contains("opus"))
            return version >= 4.5m ? OpusCurrent : OpusLegacy;
        if (id.Contains("haiku"))
            return version >= 4.5m ? Haiku45 : version >= 3.5m ? Haiku35 : Haiku3;
        if (id.Contains("sonnet"))
            return version >= 5m ? Sonnet5 : SonnetLegacy;
        return SonnetLegacy;
    }

    /// <summary>
    /// The cost of one API response. Fast mode is billed at 2x on the models
    /// that support it (Opus 4.8 and 5: $10/$50); US-only inference at 1.1x.
    /// </summary>
    public static decimal CostOf(string? modelId, long input, long output, long cacheRead,
        long cacheWrite5m, long cacheWrite1h, bool fast = false, bool usOnly = false)
    {
        var p = For(modelId);
        var cost = (input * p.Input + output * p.Output + cacheRead * p.CacheRead +
                    cacheWrite5m * p.CacheWrite5m + cacheWrite1h * p.CacheWrite1h) / 1_000_000m;
        if (fast && SupportsFastPricing(modelId))
            cost *= 2m;
        if (usOnly)
            cost *= 1.1m;
        return cost;
    }

    private static bool SupportsFastPricing(string? modelId)
    {
        var id = (modelId ?? "").ToLowerInvariant();
        return id.Contains("opus") && Version(id) >= 4.8m;
    }

    /// <summary>"claude-opus-4-6" → 4.6, "claude-sonnet-5" → 5,
    /// "claude-3-5-haiku-20241022" → 3.5; 0 if there's no version.</summary>
    private static decimal Version(string id)
    {
        var bracket = id.IndexOf('[');
        if (bracket > 0) id = id[..bracket];
        var parts = id.Split('-').Where(p => p.Length is > 0 and <= 2 && p.All(char.IsDigit)).Take(2).ToList();
        if (parts.Count == 0) return 0;
        var text = parts.Count == 1 ? parts[0] : $"{parts[0]}.{parts[1]}";
        return decimal.TryParse(text, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
