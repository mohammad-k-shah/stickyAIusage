using System.Globalization;

namespace StickyAIusage.ViewModels;

/// <summary>Display formatting shared by the widget's screens. Plain .NET (no
/// WPF types) so it's covered by the cross-platform unit tests.</summary>
public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Compact token counts: 950, 15.8k, 247k, 3.3M, 1M.</summary>
    public static string Compact(long n)
    {
        if (n < 0) return "-" + Compact(-n);
        if (n < 1_000) return n.ToString(Inv);
        if (n < 999_950) return Scaled(n / 1e3, "k");
        if (n < 999_950_000) return Scaled(n / 1e6, "M");
        return Scaled(n / 1e9, "B");

        static string Scaled(double v, string suffix) =>
            (v >= 99.95 ? v.ToString("0", Inv) : v.ToString("0.#", Inv)) + suffix;
    }

    public static string Usd(decimal amount) =>
        amount > 0 && amount < 0.01m ? "<$0.01" : "$" + amount.ToString("0.00", Inv);

    /// <summary>An amount in the given ISO 4217 currency: "$38.94" for USD
    /// (or no currency), "€12.00" / "£3.10" for the common ones, "12.00 CAD" otherwise.</summary>
    public static string Money(decimal amount, string? currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
        if (code == "USD") return Usd(amount);
        var number = amount.ToString("0.00", Inv);
        return code switch
        {
            "EUR" => "€" + number,
            "GBP" => "£" + number,
            _ => $"{number} {code}"
        };
    }

    /// <summary>"Pro", "Max", "Enterprise" from the credentials' subscriptionType.</summary>
    public static string? PlanName(string? subscriptionType) =>
        string.IsNullOrWhiteSpace(subscriptionType) ? null
            : string.Join(' ', subscriptionType.Trim().Split('_', '-', ' ')
                .Where(p => p.Length > 0)
                .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));

    /// <summary>One decimal below 100, e.g. "63.9%", "100%".</summary>
    public static string Percent(double p) =>
        (p >= 99.95 ? p.ToString("0", Inv) : p.ToString("0.#", Inv)) + "%";

    public static string WholePercent(double p) => Math.Round(p).ToString("0", Inv) + "%";

    /// <summary>"45s", "7m", "1h 43m", "2d 3h".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        if (t.TotalDays < 1) return t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes}m";
        return t.Hours == 0 ? $"{(int)t.TotalDays}d" : $"{(int)t.TotalDays}d {t.Hours}h";
    }

    public static string Ago(TimeSpan t) =>
        t.TotalSeconds < 45 ? "just now" : Duration(t) + " ago";

    /// <summary>"claude-sonnet-5" → "Sonnet 5", "claude-opus-4-8" → "Opus 4.8",
    /// "claude-haiku-4-5-20251001" → "Haiku 4.5", "claude-3-5-sonnet-20241022"
    /// → "Sonnet 3.5".</summary>
    public static string ModelName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return "Unknown model";

        var id = modelId.Trim().ToLowerInvariant();
        var bracket = id.IndexOf('[');
        if (bracket > 0) id = id[..bracket];
        if (id.StartsWith("claude-")) id = id["claude-".Length..];

        string? family = null;
        var version = new List<string>();
        foreach (var part in id.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 8 && part.All(char.IsDigit))
                continue; // date snapshot suffix
            if (part.All(char.IsDigit))
                version.Add(part);
            else
                family ??= part;
        }

        if (family is null) return modelId;
        var name = char.ToUpperInvariant(family[0]) + family[1..];
        return version.Count == 0 ? name : $"{name} {string.Join('.', version)}";
    }

    /// <summary>Claude Code's effort level: "high" → "High", "xhigh" →
    /// "Extra high", "max" → "Max", "ultracode" → "Ultracode"; "Default" when
    /// the transcript doesn't record one.</summary>
    public static string Effort(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        null or "" => "Default",
        "xhigh" or "extra_high" or "extra-high" => "Extra high",
        var l => char.ToUpperInvariant(l[0]) + l[1..]
    };

    /// <summary>"Resets in 2h 6m" for a reset that's coming up.</summary>
    public static string ResetsIn(TimeSpan untilReset) => "Resets in " + Duration(untilReset);

    /// <summary>"Resets Fri 3:30 PM" - the day and local time, like Claude's
    /// Settings → Usage page; "Resets today 5:20 PM" when it's today, and the
    /// date too when it's a week or more away.</summary>
    public static string ResetsAt(DateTimeOffset reset, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var at = TimeZoneInfo.ConvertTime(reset, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = at.ToString("h:mm tt", Inv);
        if (at.Date == today) return $"Resets today {time}";
        if ((at.Date - today).TotalDays >= 7) return $"Resets {at.ToString("MMM d", Inv)}, {time}";
        return $"Resets {at.ToString("ddd", Inv)} {time}";
    }

    /// <summary>A project folder name for display, with spaces between joined
    /// words: "StickyUsage" → "Sticky Usage", "myAIProject" → "my AI Project".
    /// Names with separators ("acme-webapp", "my_app") are left alone.</summary>
    public static string ProjectName(string folder)
    {
        if (string.IsNullOrEmpty(folder) || folder.Any(c => c is '-' or '_' or ' ' or '.'))
            return folder;
        var sb = new System.Text.StringBuilder(folder.Length + 4);
        for (var i = 0; i < folder.Length; i++)
        {
            var c = folder[i];
            if (i > 0 && char.IsUpper(c))
            {
                var prev = folder[i - 1];
                var nextIsLower = i + 1 < folder.Length && char.IsLower(folder[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
