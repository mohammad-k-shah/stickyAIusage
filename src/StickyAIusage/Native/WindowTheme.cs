using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace StickyAIusage.Native;

/// <summary>
/// Makes a standard window's native title bar match the app's theme, instead
/// of a white Windows title bar above dark content. Uses documented
/// DWM window attributes; each is silently ignored by Windows versions that
/// don't support it.
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;          // Windows 10 20H1+ and Windows 11
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;    // Windows 10 1809-1909
    private const int DwmwaCaptionColor = 35;                  // Windows 11

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call from OnSourceInitialized (the window needs an HWND).
    /// Call it again when the app's theme changes.</summary>
    public static void ApplyTitleBar(Window window, Color captionColor, bool dark = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            var enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

            var colorRef = captionColor.R | (captionColor.G << 8) | (captionColor.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Not on Windows desktop DWM - keep the default title bar.
        }
    }
}
