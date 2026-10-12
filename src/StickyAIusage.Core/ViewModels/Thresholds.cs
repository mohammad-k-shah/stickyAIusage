using System.Globalization;
using StickyAIusage.Services;

namespace StickyAIusage.ViewModels;

/// <summary>Validates the three spend-alert thresholds entered in Settings.
/// Previously any value was accepted (and unparseable input silently
/// ignored), so e.g. Yellow=90 / Orange=80 meant yellow could never show.</summary>
public static class Thresholds
{
    /// <summary>Green / yellow / orange / red for a usage percentage, using
    /// the thresholds from Settings.</summary>
    public static UsageLevel LevelFor(double percent, AppSettings settings) =>
        percent >= settings.RedThresholdPercent ? UsageLevel.Red
        : percent >= settings.OrangeThresholdPercent ? UsageLevel.Orange
        : percent >= settings.YellowThresholdPercent ? UsageLevel.Yellow
        : UsageLevel.Green;

    [Flags]
    public enum Field { None = 0, Yellow = 1, Orange = 2, Red = 4 }

    public sealed record Result(bool IsValid, double Yellow, double Orange, double Red, Field InvalidFields, string? Error);

    public static Result Validate(string yellow, string orange, string red, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var invalid = Field.None;

        var y = Parse(yellow, Field.Yellow);
        var o = Parse(orange, Field.Orange);
        var r = Parse(red, Field.Red);
        if (invalid != Field.None)
            return new Result(false, y, o, r, invalid, "Enter a whole or decimal number between 1 and 100.");

        if (!(y < o))
            return new Result(false, y, o, r, Field.Yellow | Field.Orange, "Yellow must be lower than orange.");
        if (!(o < r))
            return new Result(false, y, o, r, Field.Orange | Field.Red, "Orange must be lower than red.");

        return new Result(true, y, o, r, Field.None, null);

        double Parse(string text, Field field)
        {
            if (double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, culture, out var v) && v is >= 1 and <= 100)
                return v;
            invalid |= field;
            return 0;
        }
    }
}

public enum UsageLevel { Green, Yellow, Orange, Red }
