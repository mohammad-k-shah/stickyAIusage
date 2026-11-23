using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Alerts;

/// <summary>What an alert is about. <see cref="NeedsYou"/> and
/// <see cref="AgentDone"/> come from the session lights (Claude Code sessions).</summary>
public enum AlertKind { Threshold, ForecastHit, LimitReached, WindowReset, WeeklyRunway, SpendPace, NeedsYou, AgentDone, Test }

/// <summary>One notification. <paramref name="Key"/> is what "already told"
/// remembers it by; <paramref name="Level"/> is how urgent it is (green for
/// good news).</summary>
public sealed record Alert(string Key, AlertKind Kind, string Title, string Body, UsageLevel Level);

/// <summary>One provider's reading and the pace forecasts worked out from it.</summary>
public sealed record AlertInput(ProviderUsage Usage, IReadOnlyList<PaceForecast> Forecasts);

/// <summary>The alert switches of <see cref="AppSettings"/>, read once per
/// refresh. A kind is planned only when <see cref="Enabled"/> and its own
/// switch are on; quiet hours and the mute still plan it but drop it.</summary>
public sealed record AlertSettings(
    bool Enabled,
    bool OnThreshold,
    bool OnForecast,
    bool OnLimit,
    bool OnReset,
    double OrangePercent,
    double RedPercent,
    TimeSpan ForecastLead,
    ForecastConfidence ForecastFloor,
    QuietHours? Quiet,
    DateTimeOffset? MutedUntil,
    bool OnNeedsYou,
    bool OnDone,
    TimeSpan AttentionDelay)
{
    /// <summary>Everything on, the default thresholds, no quiet hours.</summary>
    public static AlertSettings Default { get; } = From(new AppSettings());

    public static AlertSettings From(AppSettings settings) => new(
        settings.AlertsEnabled,
        settings.AlertOnThreshold,
        settings.AlertOnForecast,
        settings.AlertOnLimit,
        settings.AlertOnReset,
        settings.OrangeThresholdPercent,
        settings.RedThresholdPercent,
        TimeSpan.FromMinutes(Math.Clamp(settings.AlertForecastLeadMinutes, 1, 300)),
        settings.ForecastFloor,
        QuietHours.FromSettings(settings.AlertQuietFrom, settings.AlertQuietTo),
        settings.AlertsMutedUntil,
        settings.AlertOnNeedsYou,
        settings.AlertOnDone,
        TimeSpan.FromSeconds(Math.Clamp(settings.AttentionDelaySeconds, 0, 3600)));

    /// <summary>True when alerts are dropped right now: inside the quiet
    /// hours, or muted from the tray menu.</summary>
    public bool IsSilenced(DateTimeOffset now, TimeZoneInfo zone) =>
        MutedUntil is { } until && until > now || Quiet?.Contains(now, zone) == true;
}
