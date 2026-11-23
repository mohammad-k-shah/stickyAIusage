namespace StickyAIusage.Services;

/// <summary>The estimated cost of one response, and whether it is rough: the
/// model wasn't in the price list, so another row's prices were used.</summary>
public readonly record struct CostEstimate(decimal Usd, bool IsRough);

/// <summary>
/// Estimates what a response cost from its token counts, since session
/// transcripts carry tokens but no cost. A best-effort estimate at list prices,
/// not the billed amount (the monthly figure from Anthropic is authoritative).
/// The prices are data: <see cref="ModelCatalog"/> (<c>data/models.json</c>).
/// </summary>
public static class PriceTable
{
    /// <summary>Standard prices for a model id such as "claude-opus-5-5",
    /// "claude-sonnet-4-6[1m]" or "claude-3-5-haiku-20241022". A model the
    /// price list doesn't know gets its family's newest prices, or the
    /// fallback row's (Sonnet 4.6 prices).</summary>
    public static ModelPrices For(string? modelId) => ModelCatalog.Current.Find(modelId).Row.Prices;

    /// <summary>
    /// The cost of one API response. Fast mode is billed at the row's fast
    /// multiplier (2x on Fable 5.2, Opus 5.5, Opus 5 and 4.8); US-only inference
    /// at 1.1x; a long prompt at the row's long-prompt prices (Haiku 5.5 above
    /// 100,000 prompt tokens, counting input, cache reads and cache writes).
    /// </summary>
    public static decimal CostOf(string? modelId, long input, long output, long cacheRead,
        long cacheWrite5m, long cacheWrite1h, bool fast = false, bool usOnly = false) =>
        Estimate(modelId, input, output, cacheRead, cacheWrite5m, cacheWrite1h, fast, usOnly).Usd;

    /// <summary>Like <see cref="CostOf"/>, and says whether the price was a guess.</summary>
    public static CostEstimate Estimate(string? modelId, long input, long output, long cacheRead,
        long cacheWrite5m, long cacheWrite1h, bool fast = false, bool usOnly = false, ModelCatalog? catalog = null)
    {
        catalog ??= ModelCatalog.Current;
        var match = catalog.Find(modelId);
        var row = match.Row;
        var p = row.PricesFor(input + cacheRead + cacheWrite5m + cacheWrite1h);
        var cost = (input * p.Input + output * p.Output + cacheRead * p.CacheRead +
                    cacheWrite5m * p.CacheWrite5m + cacheWrite1h * p.CacheWrite1h) / 1_000_000m;
        if (fast && row.FastMultiplier is { } multiplier)
            cost *= multiplier;
        if (usOnly)
            cost *= catalog.UsOnlyMultiplier;
        return new CostEstimate(cost, !match.IsKnown);
    }
}
