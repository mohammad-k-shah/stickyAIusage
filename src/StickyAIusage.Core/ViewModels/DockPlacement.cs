using StickyAIusage.Services;

namespace StickyAIusage.ViewModels;

/// <summary>Where the Windows pill lives (<see cref="AppSettings.DockMode"/>).</summary>
public enum DockMode
{
    /// <summary>Inside the taskbar, just left of the tray icons (the default).</summary>
    Taskbar,

    /// <summary>In the work-area corner just above the tray (v1's spot).</summary>
    Above,

    /// <summary>Wherever it was dragged (set by a drag, cleared by Reset widget position).</summary>
    Free
}

public static class DockModes
{
    /// <summary>"taskbar", "above" or "free"; anything else is the default, the taskbar.</summary>
    public static DockMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "above" => DockMode.Above,
        "free" => DockMode.Free,
        _ => DockMode.Taskbar
    };

    public static string ToSetting(this DockMode mode) => mode switch
    {
        DockMode.Above => "above",
        DockMode.Free => "free",
        _ => "taskbar"
    };

    /// <summary>Settings → Widget position's line under the choice.</summary>
    public static string SettingsHint(DockMode mode) => mode switch
    {
        DockMode.Taskbar => "Inside the taskbar, left of the tray icons; above it when the taskbar has no room. Drag it anywhere to leave it there.",
        DockMode.Above => "Just above the tray icons. Drag it anywhere to leave it there.",
        _ => "Where you dragged it. Choose a place to dock it again, or Reset."
    };
}

/// <summary>One monitor's taskbar, in the pill window's device-independent pixels.</summary>
/// <param name="Taskbar">The taskbar window's rectangle.</param>
/// <param name="Tray">The notification area (clock, tray icons); null on a secondary taskbar, which has none.</param>
/// <param name="ButtonsRight">Where the app buttons (Start, pinned and running apps) end on the right;
/// null when UI Automation couldn't tell (the gap is then taken as free).</param>
/// <param name="Monitor">The monitor's full rectangle.</param>
/// <param name="WorkArea">The monitor's work area (without the taskbar, unless it hides itself).</param>
/// <param name="AutoHide">The taskbar hides itself until the pointer reaches it.</param>
public sealed record TaskbarFacts(Box Taskbar, Box? Tray, double? ButtonsRight, Box Monitor, Box WorkArea, bool AutoHide = false);

/// <summary>Where the pill goes: its bottom-right corner, its height, and the mode it ended up in
/// (the taskbar falls back to above when there is no room). <paramref name="Reason"/> says why it fell back.</summary>
public sealed record DockSpot(DockMode Mode, double Right, double Bottom, double Height, string? Reason = null);

/// <summary>
/// Places the Windows pill in the taskbar, left of the tray icons and vertically centered, or in the
/// work-area corner above it. Pure: the taskbar, tray, app buttons and monitor come in as rectangles
/// (the window's DIPs), so every case is a unit test.
/// </summary>
public static class DockPlacement
{
    /// <summary>The pill's height outside the taskbar, and its most inside it.</summary>
    public const double FullHeight = 32;

    /// <summary>The least a pill may be, in a short taskbar.</summary>
    public const double MinHeight = 24;

    /// <summary>Space kept free between the pill and the tray, and between the pill and the last app button.</summary>
    public const double TaskbarGap = 4;

    /// <summary>Space from the work area's edges when above the taskbar.</summary>
    public const double CornerGap = 8;

    /// <summary>A secondary taskbar has its clock at the right end and no tray window: keep this much free there.</summary>
    public const double SecondaryClockWidth = 120;

    public const string NoRoomReason = "no room in the taskbar";
    public const string AutoHideReason = "the taskbar hides itself";
    public const string NoTaskbarReason = "no taskbar found";

    /// <summary>The pill's height in a taskbar of that height: 8 less, between 24 and 32.</summary>
    public static double HeightIn(double taskbarHeight) => Math.Clamp(taskbarHeight - 8, MinHeight, FullHeight);

    /// <summary>The spot for <paramref name="wanted"/> (Taskbar or Above; Free is the caller's saved spot).</summary>
    /// <param name="pillWidth">The pill's width in its compact (taskbar) form.</param>
    /// <param name="fallbackWorkArea">The work area to use without a taskbar.</param>
    public static DockSpot Place(DockMode wanted, TaskbarFacts? facts, double pillWidth, Box fallbackWorkArea)
    {
        if (facts is null)
            return Above(fallbackWorkArea, taskbarOnTop: false, wanted == DockMode.Taskbar ? NoTaskbarReason : null);
        var onTop = facts.Taskbar.Y <= facts.Monitor.Y + 1 && facts.Taskbar.Height < facts.Monitor.Height / 2;
        if (wanted != DockMode.Taskbar)
            return Above(facts.WorkArea, onTop, null);
        if (facts.AutoHide || !IsShownOn(facts.Taskbar, facts.Monitor))
            return Above(facts.WorkArea, onTop, AutoHideReason);

        var height = HeightIn(facts.Taskbar.Height);
        var right = (facts.Tray is { } tray ? tray.X : facts.Taskbar.Right - SecondaryClockWidth) - TaskbarGap;
        var left = right - pillWidth;
        var leftLimit = Math.Max(facts.Taskbar.X, facts.ButtonsRight ?? facts.Taskbar.X) + TaskbarGap;
        if (left < leftLimit)
            return Above(facts.WorkArea, onTop, NoRoomReason);
        var bottom = facts.Taskbar.Y + (facts.Taskbar.Height + height) / 2;
        return new DockSpot(DockMode.Taskbar, right, bottom, height);
    }

    /// <summary>The corner by the tray: above a bottom taskbar, below a top one.</summary>
    private static DockSpot Above(Box work, bool taskbarOnTop, string? reason) =>
        new(DockMode.Above, work.Right - CornerGap,
            taskbarOnTop ? work.Y + CornerGap + FullHeight : work.Bottom - CornerGap, FullHeight, reason);

    /// <summary>Most of the taskbar is on its monitor (an auto-hidden one slides almost all the way off).</summary>
    private static bool IsShownOn(Box taskbar, Box monitor)
    {
        var visibleHeight = Math.Min(taskbar.Bottom, monitor.Bottom) - Math.Max(taskbar.Y, monitor.Y);
        return taskbar.Height > 0 && visibleHeight >= taskbar.Height * 0.75;
    }
}

/// <summary>The monitor choices of Settings → Widget position and the tray's "Move widget to".</summary>
public static class DockMonitors
{
    /// <summary>"Main display" for the primary one; Windows' "\\.\DISPLAY2" reads "Display 2".</summary>
    public static string Label(string device, bool isPrimary)
    {
        if (isPrimary)
            return "Main display";
        var name = device.StartsWith(@"\\.\", StringComparison.Ordinal) ? device[4..] : device;
        var digits = name.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ? name[7..] : "";
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? "Display " + digits : name;
    }

    /// <summary>The choices for <see cref="AppSettings.DockMonitor"/>: the main display ("primary") first.</summary>
    public static IReadOnlyList<(string Label, string Setting)> Choices(IReadOnlyList<(string Device, bool IsPrimary)> monitors) =>
        [("Main display", AppSettings.PrimaryMonitor),
         .. monitors.Where(m => !m.IsPrimary).Select(m => (Label(m.Device, false), m.Device))];

    /// <summary>"Move widget to"'s taskbar items: one "Taskbar" (keeping the chosen monitor) on one
    /// monitor, "Taskbar on …" per monitor on several.</summary>
    public static IReadOnlyList<(string Header, string? Device)> TaskbarMenu(IReadOnlyList<(string Device, bool IsPrimary)> monitors) =>
        monitors.Count <= 1
            ? [("Taskbar", null)]
            : Choices(monitors).Select(c => ("Taskbar on " + (c.Setting == AppSettings.PrimaryMonitor ? "the main display" : c.Label), (string?)c.Setting)).ToList();

    /// <summary>The choice that matches the setting; a monitor that isn't connected reads as the main display.</summary>
    public static string Selected(string? setting, IReadOnlyList<(string Device, bool IsPrimary)> monitors) =>
        monitors.Any(m => !m.IsPrimary && string.Equals(m.Device, setting, StringComparison.OrdinalIgnoreCase))
            ? monitors.First(m => string.Equals(m.Device, setting, StringComparison.OrdinalIgnoreCase)).Device
            : AppSettings.PrimaryMonitor;
}

/// <summary>When the pill steps aside for a full-screen app or a presentation.</summary>
public static class FullScreenRule
{
    // SHQueryUserNotificationState (shellapi.h, QUERY_USER_NOTIFICATION_STATE).
    public const int NotPresent = 1, Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4, AcceptsNotifications = 5;

    /// <param name="state">What Windows says about notifications now.</param>
    /// <param name="foregroundFillsPillMonitor">The foreground window covers the whole monitor the pill is on.</param>
    public static bool ShouldHide(bool enabled, int state, bool foregroundFillsPillMonitor) =>
        enabled && (state == PresentationMode ||
                    (state is Busy or RunningD3DFullScreen && foregroundFillsPillMonitor));
}
