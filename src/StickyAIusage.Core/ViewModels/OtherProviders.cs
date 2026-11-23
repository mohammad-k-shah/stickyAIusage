using System.Globalization;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.ViewModels;

/// <summary>Another tool's reading for one refresh (Codex), with the pace of its windows.</summary>
public sealed record ProviderReading(ProviderUsage Usage, IReadOnlyList<PaceForecast> Forecasts);

/// <summary>
/// One more tool in the flyout's usage card, under Claude's rows: a title
/// ("Codex · Plus"), when it was read, where the numbers come from, an
/// optional message ("Codex does not report limits in this mode") and the
/// same rows as Claude's (bar, reset, pace line).
/// </summary>
public sealed record ProviderSection(string ProviderId, string Title, string? UpdatedText, string? SourceLabel,
    string? Message, IReadOnlyList<SummaryRow> Rows)
{
    /// <summary>What a screen reader says for the card (UI Automation names a list item by it).</summary>
    public override string ToString() => Title;
}

/// <summary>"Codex 91%" after the pill's usage, in the level's color: another
/// tool is closer to its limit than Claude.</summary>
public sealed record ProviderChip(string ProviderId, string Text, UsageLevel Level, string Tooltip);

/// <summary>Builds the sections and chips both apps show for the other tools.</summary>
public static class OtherProviders
{
    /// <summary>A tool whose newest numbers are older than this isn't in use: its card would only
    /// show a long-passed reset (seen on a Mac: a Codex card "As of 290 days ago"). It comes back
    /// with the tool's next use.</summary>
    public static readonly TimeSpan DormantAfter = TimeSpan.FromDays(14);

    /// <summary>One section per reading, in the order given; a tool not used for
    /// <see cref="DormantAfter"/> has none.</summary>
    public static IReadOnlyList<ProviderSection> From(IReadOnlyList<ProviderReading> readings, AppSettings settings,
        DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null) =>
        readings.Where(r => !IsDormant(r.Usage, now)).Select(r => Section(r, settings, now, zone, culture)).ToList();

    /// <summary>The reading has numbers, and they are older than <see cref="DormantAfter"/>.</summary>
    public static bool IsDormant(ProviderUsage usage, DateTimeOffset now) =>
        usage.Windows.Count > 0 && now - usage.ObservedAt > DormantAfter;

    private static ProviderSection Section(ProviderReading reading, AppSettings settings, DateTimeOffset now,
        TimeZoneInfo? zone, CultureInfo? culture)
    {
        var usage = reading.Usage;
        var pace = new WidgetViewModel.PaceText(settings.ShowForecast ? reading.Forecasts : [], settings.ForecastFloor, now, zone, culture);
        var rows = new List<SummaryRow>();
        var hasNumbers = usage.Health is (UsageHealth.Ok or UsageHealth.Stale) && usage.Windows.Count > 0;
        if (hasNumbers)
        {
            foreach (var window in usage.Windows)
            {
                var (percent, detail) = Current(window, now, zone, culture);
                var withDay = window.Kind != UsageWindowKinds.FiveHour;
                rows.Add(new SummaryRow(LabelOf(usage, window.Kind), $"{Format.WholePercent(percent)} · {detail}",
                    Math.Min(100, percent), Thresholds.LevelFor(percent, settings),
                    WidgetViewModel.PaceText.Join(pace.PaceLine(window.Kind, withDay),
                        window.Kind == UsageWindowKinds.SevenDay ? pace.EvenPace(window.Kind) : null)));
            }
        }
        var updated = !hasNumbers ? null
            : (usage.Health == UsageHealth.Stale ? "As of " : "Updated ") + Format.Ago(now - usage.ObservedAt);
        var title = usage.PlanName is { } plan ? $"{usage.DisplayName} · {plan}" : usage.DisplayName;
        return new ProviderSection(usage.ProviderId, title, updated, usage.SourceLabel, usage.HealthDetail, rows);
    }

    /// <summary>The percentage to show now and the reset detail: a window whose
    /// reset passed has started over (0%, "Reset passed").</summary>
    private static (double Percent, string Detail) Current(UsageWindow window, DateTimeOffset now, TimeZoneInfo? zone,
        CultureInfo? culture) => window.ResetsAt switch
    {
        null => (window.PercentUsed, "Reset time unknown"),
        { } reset when reset <= now => (0, "Reset passed"),
        { } reset when window.Kind == UsageWindowKinds.FiveHour => (window.PercentUsed, Format.ResetsIn(reset - now)),
        { } reset => (window.PercentUsed, Format.ResetsAt(reset, now, zone, culture))
    };

    /// <summary>The provider's own name for the window, else one from its kind.</summary>
    public static string LabelOf(ProviderUsage usage, string kind) =>
        usage.WindowLabels.TryGetValue(kind, out var label) ? label : Format.WindowName(kind);

    /// <summary>
    /// A chip per other tool that is closer to a limit than Claude and at or
    /// past the orange threshold, the fullest first. Claude without a reading
    /// counts as 0%. A stale reading still counts (its windows say as of when);
    /// a window whose reset passed counts as 0%.
    /// </summary>
    /// <param name="claudePercent">Claude's fullest window (the pill's ring).</param>
    public static IReadOnlyList<ProviderChip> Chips(IReadOnlyList<ProviderReading> readings, double claudePercent,
        AppSettings settings, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        var chips = new List<(double Percent, ProviderChip Chip)>();
        foreach (var reading in readings)
        {
            var usage = reading.Usage;
            if (usage.Health is not (UsageHealth.Ok or UsageHealth.Stale) || IsDormant(usage, now))
                continue;
            var fullest = usage.Windows
                .Select(w => (Window: w, Now: Current(w, now, zone, culture)))
                .OrderByDescending(w => w.Now.Percent)
                .FirstOrDefault();
            if (fullest.Window is null)
                continue;
            var percent = fullest.Now.Percent;
            if (percent < settings.OrangeThresholdPercent || percent <= claudePercent)
                continue;
            var asOf = usage.Health == UsageHealth.Stale ? $"\nAs of {Format.Ago(now - usage.ObservedAt)}" : "";
            chips.Add((percent, new ProviderChip(usage.ProviderId, $"{usage.DisplayName} {Format.WholePercent(percent)}",
                Thresholds.LevelFor(percent, settings),
                $"{usage.DisplayName} {Lower(LabelOf(usage, fullest.Window.Kind))}: {Format.WholePercent(percent)} · {fullest.Now.Detail}{asOf}")));
        }
        return chips.OrderByDescending(c => c.Percent).Select(c => c.Chip).ToList();
    }

    /// <summary>The hint under Settings → Codex: what it reads, or that Codex isn't here.</summary>
    public static string CodexSettingsHint(bool present) => present
        ? "Codex's plan limits from its local session log (~/.codex/sessions), under Claude's in the flyout"
        : "Not found on this computer (~/.codex/sessions); shows by itself once Codex runs here";

    private static string Lower(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
