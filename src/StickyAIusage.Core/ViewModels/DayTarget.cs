using System.Globalization;
using StickyAIusage.Models;

namespace StickyAIusage.ViewModels;

/// <summary>The day target's rules, shared by both Settings screens.</summary>
public static class DayTarget
{
    public const decimal MaxUsd = 100_000m;

    /// <summary>A daily dollar target only makes sense against a fixed
    /// monthly spend cap. Plans with rolling 5-hour and weekly windows don't
    /// spend dollars, so it's off for them. Before the first reading the
    /// plan isn't known yet, so it stays editable.</summary>
    public static bool AppliesTo(UsageMode mode) => mode != UsageMode.PlanLimits;

    public const string NotApplicableNote =
        "Day target isn't available on plans with rolling 5-hour and weekly limits (Pro, Max), " +
        "so it's turned off. It only applies to a fixed monthly spend cap.";

    public sealed record Result(bool IsValid, decimal Usd, string? Error);

    /// <summary>A dollar amount above zero; "$10", "12.50" and "7" are all fine.</summary>
    public static Result Validate(string text, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var trimmed = text.Trim().TrimStart('$').Trim();
        if (decimal.TryParse(trimmed, NumberStyles.Number, culture, out var usd) && usd > 0 && usd <= MaxUsd)
            return new Result(true, Math.Round(usd, 2), null);
        return new Result(false, 0, "Enter a daily target in dollars, above $0 (for example 10 or 12.50).");
    }

    public static string ToText(decimal usd, CultureInfo? culture = null) =>
        usd.ToString("0.##", culture ?? CultureInfo.CurrentCulture);
}
