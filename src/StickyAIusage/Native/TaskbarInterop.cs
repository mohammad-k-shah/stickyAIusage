using System.Runtime.InteropServices;

namespace StickyAIusage.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

/// <summary>
/// Locates the Windows taskbar and its system-tray notification area so the
/// docked strip can be placed immediately to its left. Windows 11 gives third-party apps no supported API to
/// inject content into the taskbar itself, so this reads the taskbar's window
/// geometry and places a separate always-on-top window next to it - the same
/// approach used by other taskbar-adjacent utilities.
/// </summary>
public static class TaskbarInterop
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

    /// <summary>
    /// Puts the window back at the top of the topmost band, without activating
    /// it. The taskbar is itself topmost and is raised above every other
    /// topmost window whenever it is clicked (or a tray menu closes, or
    /// Explorer restarts), so a pill sitting on or near it gets buried until
    /// something re-asserts it. Skipped while the foreground window is one of
    /// our own (the flyout, a tray menu) or a shell flyout (Start, Quick
    /// Settings, the overflow tray), which the pill must not draw over.
    /// </summary>
    public static void KeepOnTop(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out var pid);
            if (pid == (uint)Environment.ProcessId)
                return;
            var name = new System.Text.StringBuilder(64);
            GetClassName(foreground, name, name.Capacity);
            if (name.ToString() is "Windows.UI.Core.CoreWindow" or "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow")
                return;
        }
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>Returns the taskbar's screen rect, or null if it can't be found
    /// (e.g. running before Explorer has started the taskbar).</summary>
    public static RECT? GetTaskbarRect()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;
        return GetWindowRect(taskbar, out var rect) ? rect : null;
    }

    /// <summary>Returns the tray notification area's screen rect (the cluster
    /// containing the clock, volume, network and other tray icons), or null if
    /// it can't be found.</summary>
    public static RECT? GetTrayNotificationAreaRect()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;

        var trayNotify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify == IntPtr.Zero) return null;

        return GetWindowRect(trayNotify, out var rect) ? rect : null;
    }
}
