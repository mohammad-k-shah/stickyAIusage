namespace StickyAIusage.ViewModels;

/// <summary>A screen rectangle in whatever units the caller uses (WPF DIPs,
/// Avalonia pixels) - the placement math only needs them to be consistent.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Where the pill and the open flyout go, and how tall the flyout may be.</summary>
public sealed record FlyoutLayout(Box Pill, Box Flyout, bool OpensAbove, double MaxFlyoutHeight);

/// <summary>
/// Places the flyout next to the pill so that both are always fully on
/// screen, wherever the pill was dragged:
/// - The pill is pulled back onto the screen if it hangs off an edge.
/// - The flyout opens on the side with room for it: above or below, starting
///   with the platform's natural side (above the Windows taskbar, below the
///   macOS menu bar). If neither side fits it whole, it takes the roomier side
///   and scrolls.
/// - It lines up with the pill's right edge, shifted sideways as needed to
///   stay inside the work area.
/// The pill may sit outside the work area (on the taskbar or in the menu bar),
/// so it's kept within the full screen, and the flyout within the work area.
/// </summary>
public static class FlyoutPlacement
{
    /// <summary>Never squeeze the flyout below this, even on a tiny screen.</summary>
    public const double MinFlyoutHeight = 160;

    public static bool WouldOpenAbove(Box pill, double flyoutHeight, Box workArea, bool preferAbove, double gap)
    {
        var above = pill.Y - gap - workArea.Y;
        var below = workArea.Bottom - (pill.Bottom + gap);
        return preferAbove
            ? above >= flyoutHeight || above >= below
            : !(below >= flyoutHeight || below >= above);
    }

    /// <param name="forceAbove">Keeps the side chosen when the flyout opened
    /// while its content loads and grows, so it doesn't jump.</param>
    /// <param name="maxHeightCap">An upper bound on the flyout's height (e.g.
    /// 75% of the screen) so it never fills a small screen top to bottom; its
    /// sessions list scrolls instead.</param>
    public static FlyoutLayout Place(Box pill, double flyoutWidth, double flyoutHeight, Box screen, Box workArea,
        bool preferAbove, double gap, bool? forceAbove = null, double? maxHeightCap = null)
    {
        var p = pill with
        {
            X = Clamp(pill.X, screen.X, screen.Right - pill.Width),
            Y = Clamp(pill.Y, screen.Y, screen.Bottom - pill.Height)
        };

        if (maxHeightCap is { } cap)
            flyoutHeight = Math.Min(flyoutHeight, Math.Max(MinFlyoutHeight, cap));
        var above = forceAbove ?? WouldOpenAbove(p, flyoutHeight, workArea, preferAbove, gap);
        var space = above ? p.Y - gap - workArea.Y : workArea.Bottom - (p.Bottom + gap);
        if (maxHeightCap is { } c)
            space = Math.Min(space, c);
        var maxHeight = Math.Max(MinFlyoutHeight, space);
        var height = Math.Min(flyoutHeight, maxHeight);

        var x = Clamp(p.Right - flyoutWidth, workArea.X, workArea.Right - flyoutWidth);
        var y = above ? p.Y - gap - height : p.Bottom + gap;
        return new FlyoutLayout(p, new Box(x, y, flyoutWidth, height), above, maxHeight);
    }

    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
