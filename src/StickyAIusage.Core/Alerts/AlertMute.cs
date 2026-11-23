using System.Globalization;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Alerts;

/// <summary>The tray and menu-bar item "Mute alerts for an hour": its words,
/// and what a click does (mute for an hour, or unmute when muted).</summary>
public static class AlertMute
{
    public static readonly TimeSpan Length = TimeSpan.FromHours(1);

    public static bool IsMuted(AppSettings settings, DateTimeOffset now) =>
        settings.AlertsMutedUntil is { } until && until > now;

    /// <summary>"Mute alerts for an hour", or "Unmute alerts (muted until 5:30 PM)".</summary>
    public static string MenuLabel(AppSettings settings, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null) =>
        IsMuted(settings, now)
            ? $"Unmute alerts (muted until {Format.ShortTime(settings.AlertsMutedUntil!.Value, zone, culture)})"
            : "Mute alerts for an hour";

    /// <summary>Mutes for an hour or unmutes; the caller saves the settings.</summary>
    public static void Toggle(AppSettings settings, DateTimeOffset now) =>
        settings.AlertsMutedUntil = IsMuted(settings, now) ? null : now + Length;
}
