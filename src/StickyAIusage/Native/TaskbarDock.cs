using System.Runtime.InteropServices;
using System.Windows.Automation;
using StickyAIusage.Services;

namespace StickyAIusage.Native;

/// <summary>One monitor, in device pixels (virtual-screen coordinates).</summary>
/// <param name="Device">Windows' device name ("\\.\DISPLAY1"): what <see cref="AppSettings.DockMonitor"/> stores.</param>
public sealed record MonitorInfo(IntPtr Handle, string Device, RECT Bounds, RECT Work, bool IsPrimary);

/// <summary>A monitor's taskbar, in device pixels: its window, its tray (none on a secondary taskbar)
/// and where its app buttons end (null when UI Automation couldn't say).</summary>
public sealed record TaskbarInfo(IntPtr Window, RECT Rect, RECT? Tray, int? ButtonsRight, bool AutoHide);

/// <summary>
/// What the pill needs to dock in the taskbar: the monitors, each monitor's taskbar (the primary one
/// is <c>Shell_TrayWnd</c>, the others <c>Shell_SecondaryTrayWnd</c>, which have a clock but no tray),
/// where the app buttons end (UI Automation, once per change, within half a second), whether the
/// taskbar hides itself, and whether a full-screen app or a presentation is on. Thin: the decisions are
/// <see cref="ViewModels.DockPlacement"/> and <see cref="ViewModels.FullScreenRule"/>, tested in Core.
/// </summary>
public static class TaskbarDock
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private const uint MonitorDefaultToNearest = 2, MonitorInfoPrimary = 1;
    private const uint AbmGetState = 4, AbsAutoHide = 1;

    /// <summary>Broadcast to top-level windows when Explorer (re)creates the taskbar.</summary>
    public static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    /// <summary>Every monitor, the primary one first.</summary>
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            if (Info(handle) is { } info)
                monitors.Add(info);
            return true;
        }, IntPtr.Zero);
        return monitors.OrderByDescending(m => m.IsPrimary).ToList();
    }

    /// <summary>The monitor a window is (mostly) on.</summary>
    public static MonitorInfo? MonitorOf(IntPtr hwnd) => Info(MonitorFromWindow(hwnd, MonitorDefaultToNearest));

    private static MonitorInfo? Info(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
            return null;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        return GetMonitorInfo(monitor, ref info)
            ? new MonitorInfo(monitor, info.szDevice, info.rcMonitor, info.rcWork, (info.dwFlags & MonitorInfoPrimary) != 0)
            : null;
    }

    /// <summary>The monitor <see cref="AppSettings.DockMonitor"/> names; the primary one when it
    /// says "primary" or that monitor isn't connected.</summary>
    public static MonitorInfo? Chosen(string? device)
    {
        var monitors = Monitors();
        return monitors.FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    /// <summary>That monitor's taskbar, or null (none found: Explorer not running, or no taskbar on it).</summary>
    /// <param name="readButtons">Read where the app buttons end (UI Automation, up to half a second).</param>
    public static TaskbarInfo? TaskbarOn(MonitorInfo monitor, bool readButtons)
    {
        foreach (var window in TaskbarWindows())
        {
            if (Info(MonitorFromWindow(window, MonitorDefaultToNearest)) is not { } on || on.Handle != monitor.Handle)
                continue;
            if (!GetWindowRect(window, out var rect))
                continue;
            RECT? tray = null;
            var notify = FindWindowEx(window, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && GetWindowRect(notify, out var trayRect))
                tray = trayRect;
            var buttons = readButtons ? ButtonsRight(window, tray, TimeSpan.FromMilliseconds(500)) : null;
            return new TaskbarInfo(window, rect, tray, buttons, IsAutoHide());
        }
        return null;
    }

    private static IEnumerable<IntPtr> TaskbarWindows()
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
            yield return primary;
        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return secondary;
    }

    /// <summary>Where the app buttons (Start, Widgets, pinned and running apps) end on the right: the
    /// rightmost button left of the tray. Null when UI Automation fails or takes longer than the budget
    /// (a Remote Desktop session can be slow); the gap is then taken as free.</summary>
    private static int? ButtonsRight(IntPtr taskbar, RECT? tray, TimeSpan budget)
    {
        var read = Task.Run(() =>
        {
            var element = AutomationElement.FromHandle(taskbar);
            var buttons = element.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var limit = tray?.Left ?? int.MaxValue;
            int? right = null;
            foreach (AutomationElement button in buttons)
            {
                var box = button.Current.BoundingRectangle;
                if (box.IsEmpty || box.Width <= 0 || box.Right > limit + 1 ||
                    button.Current.ClassName.StartsWith("SystemTray.", StringComparison.Ordinal))
                    continue;
                right = Math.Max(right ?? int.MinValue, (int)Math.Ceiling(box.Right));
            }
            return right;
        });
        try
        {
            return read.Wait(budget) ? read.Result : null;
        }
        catch (AggregateException ex)
        {
            DiagnosticLog.WriteInfo($"Taskbar: couldn't read the app buttons ({ex.InnerException?.Message}); taking the gap as free.");
            return null;
        }
    }

    /// <summary>The taskbar hides itself until the pointer reaches it.</summary>
    public static bool IsAutoHide()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return ((long)SHAppBarMessage(AbmGetState, ref data) & AbsAutoHide) != 0;
    }

    /// <summary>SHQueryUserNotificationState (<see cref="ViewModels.FullScreenRule"/>); 0 when it fails.</summary>
    public static int NotificationState() => SHQueryUserNotificationState(out var state) == 0 ? state : 0;

    /// <summary>The foreground window covers that whole monitor (a full-screen app, not the desktop or the taskbar).</summary>
    public static bool ForegroundFills(MonitorInfo monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !GetWindowRect(foreground, out var rect))
            return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        return rect.Left <= monitor.Bounds.Left && rect.Top <= monitor.Bounds.Top &&
               rect.Right >= monitor.Bounds.Right && rect.Bottom >= monitor.Bounds.Bottom;
    }
}

/// <summary>
/// Calls back (on the thread that created it, which must pump messages: the UI thread) when the
/// foreground window changes anywhere, or when the taskbar's process reorders or moves its windows -
/// the moments the taskbar may have buried the pill or changed its free space. Replaces polling.
/// </summary>
public sealed class TaskbarEvents : IDisposable
{
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private const uint EventSystemForeground = 0x0003, EventObjectReorder = 0x8004, EventObjectLocationChange = 0x800B;
    private const uint OutOfContext = 0x0000, SkipOwnProcess = 0x0002;

    private readonly WinEventProc _callback; // kept alive: Windows calls it from native code
    private readonly List<IntPtr> _hooks = [];
    private readonly Action<bool> _changed;

    /// <param name="changed">Called with true for a taskbar change (its windows moved or reordered),
    /// false for a foreground change.</param>
    public TaskbarEvents(Action<bool> changed)
    {
        _changed = changed;
        _callback = OnEvent;
        Hook();
    }

    /// <summary>Hooks again (Explorer restarted: a new process owns the taskbar).</summary>
    public void Rehook()
    {
        Unhook();
        Hook();
    }

    private void Hook()
    {
        _hooks.Add(SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, OutOfContext | SkipOwnProcess));
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && GetWindowThreadProcessId(taskbar, out var explorer) != 0 && explorer != 0)
            _hooks.Add(SetWinEventHook(EventObjectReorder, EventObjectLocationChange, IntPtr.Zero, _callback, explorer, 0, OutOfContext));
        _hooks.RemoveAll(h => h == IntPtr.Zero);
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Explorer's own windows only (its location events for the cursor or carets are idObject < 0).
        if (eventType != EventSystemForeground && idObject != 0)
            return;
        _changed(eventType != EventSystemForeground);
    }

    private void Unhook()
    {
        foreach (var hook in _hooks)
            UnhookWinEvent(hook);
        _hooks.Clear();
    }

    public void Dispose() => Unhook();
}
