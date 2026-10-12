using System.Runtime.InteropServices;
using Avalonia.Controls;
using StickyAIusage.Services;

namespace StickyAIusage.Mac.Native;

/// <summary>
/// The few AppKit / CoreGraphics calls Avalonia doesn't expose, made through
/// the Objective-C runtime directly (no Xcode or macOS workload needed):
/// - Raising the widget above the menu bar, so it can sit in the menu bar
///   strip next to the status icons, and keeping it on every Space and over
///   full-screen apps.
/// - Finding where the status icons (Wi-Fi, battery, other apps' items) start,
///   and where the camera notch is, so the pill can be placed just left of
///   them without covering either.
/// </summary>
internal static class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // NSWindow levels (CGWindowLevel.h): the menu bar is 24, status items 25.
    private const long StatusWindowLevel = 25;

    [Flags]
    private enum CollectionBehavior : ulong
    {
        CanJoinAllSpaces = 1 << 0,
        Stationary = 1 << 4,
        IgnoresCycle = 1 << 6,
        FullScreenAuxiliary = 1 << 8
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public double X, Y, Width, Height;
        public readonly double Right => X + Width;
        public override readonly string ToString() => $"({X:0},{Y:0} {Width:0}x{Height:0})";
    }

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendLong(IntPtr receiver, IntPtr selector, long value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendULong(IntPtr receiver, IntPtr selector, ulong value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr selector, bool value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern bool SendBoolRet(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>
    /// Brings the app to the front, the way a menu bar app does when its
    /// panel opens. The widget's flyout needs this: a background (LSUIElement)
    /// app isn't activated by a click on a floating window, and without being
    /// active it never gets the deactivated event that closes the flyout when
    /// you click somewhere else.
    /// </summary>
    public static void ActivateApp()
    {
        try
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != IntPtr.Zero)
                SendBool(app, sel_registerName("activateIgnoringOtherApps:"), true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacNative.ActivateApp", ex);
        }
    }

    [DllImport(CoreGraphics)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CoreGraphics)] private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);
    [DllImport(CoreGraphics)] private static extern uint CGMainDisplayID();
    [DllImport(CoreGraphics)] private static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreFoundation)] private static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CoreFoundation)] private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);

    /// <summary>Puts the window above the menu bar and on every Space.</summary>
    public static void FloatAboveMenuBar(Window window)
    {
        try
        {
            // Avalonia hands out the window's NSView; its -window is the NSWindow.
            var handle = window.TryGetPlatformHandle();
            var ns = handle is { HandleDescriptor: "NSView" } && handle.Handle != IntPtr.Zero
                ? Send(handle.Handle, sel_registerName("window"))
                : handle is { HandleDescriptor: "NSWindow" } ? handle.Handle : IntPtr.Zero;
            if (ns == IntPtr.Zero)
            {
                DiagnosticLog.WriteInfo($"MacNative: no NSWindow ({handle?.HandleDescriptor ?? "no handle"}); the widget stays at the normal floating level.");
                return;
            }
            SendLong(ns, sel_registerName("setLevel:"), StatusWindowLevel);
            SendULong(ns, sel_registerName("setCollectionBehavior:"), (ulong)(
                CollectionBehavior.CanJoinAllSpaces | CollectionBehavior.Stationary |
                CollectionBehavior.IgnoresCycle | CollectionBehavior.FullScreenAuxiliary));
            // The pill and flyout draw their own soft shadow; the system one
            // would outline the whole transparent window.
            SendBool(ns, sel_registerName("setHasShadow:"), false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacNative.FloatAboveMenuBar", ex);
        }
    }

    /// <summary>The main display's bounds in points (top-left origin).</summary>
    public static CGRect MainDisplayBounds() => CGDisplayBounds(CGMainDisplayID());

    /// <summary>
    /// The left edge (in points, main display) of the leftmost status item -
    /// the icons at the right of the menu bar - or null if none were found.
    /// Status items are windows at the status level (25) touching the top of
    /// the main display; our own window is excluded. Reading window bounds
    /// needs no Screen Recording permission (only window titles do).
    /// </summary>
    public static double? StatusItemsLeftEdge(double menuBarHeight)
    {
        const uint OnScreenOnly = 1 << 0, ExcludeDesktopElements = 1 << 4;
        const int CFNumberSInt64Type = 4;
        const uint Utf8 = 0x08000100;

        var list = IntPtr.Zero;
        var keys = new List<IntPtr>();
        try
        {
            list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
            if (list == IntPtr.Zero)
                return null;

            IntPtr Key(string name) { var k = CFStringCreateWithCString(IntPtr.Zero, name, Utf8); keys.Add(k); return k; }
            var layerKey = Key("kCGWindowLayer");
            var boundsKey = Key("kCGWindowBounds");
            var pidKey = Key("kCGWindowOwnerPID");

            var ownPid = Environment.ProcessId;
            var display = MainDisplayBounds();
            double? left = null;

            for (long i = 0, n = CFArrayGetCount(list); i < n; i++)
            {
                var window = CFArrayGetValueAtIndex(list, i);
                if (!CFNumberGetValue(CFDictionaryGetValue(window, layerKey), CFNumberSInt64Type, out var layer) || layer != StatusWindowLevel)
                    continue;
                if (CFNumberGetValue(CFDictionaryGetValue(window, pidKey), CFNumberSInt64Type, out var pid) && pid == ownPid)
                    continue;
                var boundsDict = CFDictionaryGetValue(window, boundsKey);
                if (boundsDict == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(boundsDict, out var r))
                    continue;

                // In the menu bar of the main display, and icon-sized.
                if (r.Y > display.Y + 1 || r.Height > menuBarHeight + 2 || r.Width > 400 ||
                    r.X < display.X || r.Right > display.Right + 1)
                    continue;
                // Status items sit at the right; the left of the bar is app menus.
                if (r.X < display.X + display.Width / 3)
                    continue;

                left = left is null ? r.X : Math.Min(left.Value, r.X);
            }
            return left;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacNative.StatusItemsLeftEdge", ex);
            return null;
        }
        finally
        {
            foreach (var k in keys) if (k != IntPtr.Zero) CFRelease(k);
            if (list != IntPtr.Zero) CFRelease(list);
        }
    }

    /// <summary>
    /// On a MacBook with a camera notch, the left edge (points) of the usable
    /// menu bar area to the right of the notch; null without a notch or on
    /// macOS older than 12. Uses NSScreen.auxiliaryTopRightArea, whose x is
    /// the same in AppKit's bottom-left coordinates.
    /// </summary>
    public static double? NotchRightEdge()
    {
        try
        {
            // screens[0] is the display with the menu bar.
            var screens = Send(objc_getClass("NSScreen"), sel_registerName("screens"));
            var screen = screens == IntPtr.Zero ? IntPtr.Zero : Send(screens, sel_registerName("firstObject"));
            if (screen == IntPtr.Zero)
                return null;
            var selector = sel_registerName("auxiliaryTopRightArea");
            if (!SendBoolRet(screen, sel_registerName("respondsToSelector:"), selector))
                return null;
            var area = SendRect(screen, selector);
            return area.Width > 0 ? area.X : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacNative.NotchRightEdge", ex);
            return null;
        }
    }
}
