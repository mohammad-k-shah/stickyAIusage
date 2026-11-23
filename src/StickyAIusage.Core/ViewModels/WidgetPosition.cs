using StickyAIusage.Services;

namespace StickyAIusage.ViewModels;

/// <summary>
/// Where the Windows pill starts: where it was left, or docked at the tray
/// corner. The pill is pinned by its bottom-right corner (in device-independent
/// pixels); settings store that corner plus the window's outer margin.
/// </summary>
public static class WidgetPosition
{
    /// <summary>How much of the pill must stay inside the screen.</summary>
    private const double MinVisible = 40;

    /// <summary>The saved corner to start at, or null to dock at the tray:
    /// docked in Settings, nothing saved yet, or the saved spot is no longer
    /// on any screen (a monitor was unplugged or the resolution dropped).</summary>
    /// <param name="screen">The virtual screen (all monitors).</param>
    /// <param name="windowMargin">What the settings add to the pill's corner.</param>
    public static (double Right, double Bottom)? Saved(AppSettings settings, Box screen, double windowMargin, double pillHeight)
    {
        if (settings.WidgetDocked || settings.WidgetRight is not { } right || settings.WidgetBottom is not { } bottom ||
            !double.IsFinite(right) || !double.IsFinite(bottom))
            return null;
        var corner = (Right: right - windowMargin, Bottom: bottom - windowMargin);
        return IsMostlyOnScreen(corner.Right, corner.Bottom, screen, pillHeight) ? corner : null;
    }

    /// <summary>Whether a pill with this bottom-right corner can be seen and
    /// grabbed: some of its width and most of its height inside the screen.</summary>
    public static bool IsMostlyOnScreen(double pillRight, double pillBottom, Box screen, double pillHeight) =>
        pillRight > screen.X + MinVisible && pillRight - MinVisible < screen.Right &&
        pillBottom > screen.Y + pillHeight && pillBottom - pillHeight / 2 < screen.Bottom;
}
