using System.IO;
using System.Security;
using StickyAIusage.Services;

namespace StickyAIusage.Mac.Native;

/// <summary>"Launch at login" via a per-user LaunchAgent - the macOS
/// counterpart of the Windows app's HKCU Run key. No admin rights needed;
/// launchd picks it up at the next login.</summary>
internal static class LoginItem
{
    // One agent per profile ("com.stickyaiusage.widget-dev", started with
    // --profile dev), so a dev copy never replaces the installed widget's.
    private static string Label => "com.stickyaiusage.widget" + AppPaths.Current.NameSuffix;

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(PlistPath);
                return;
            }

            if (AppPaths.Current.RelaunchArguments is not { } arguments)
            {
                DiagnosticLog.WriteInfo($"Launch at login is not available for {AppPaths.Current.InstanceLabel}: it is set through {AppPaths.DataDirectoryVariable}, which a login start doesn't have.");
                return;
            }

            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "StickyAIusage");
            // One <string> per argument, indented to sit inside <array> below.
            var programArguments = string.Concat(new[] { exe }.Concat(arguments)
                .Select(a => $"\n        <string>{SecurityElement.Escape(a)}</string>"));
            // launchd reads the file at login: a half-written plist would drop the entry.
            AtomicFile.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>{Label}</string>
                    <key>ProgramArguments</key>
                    <array>{programArguments}
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                    <key>ProcessType</key>
                    <string>Interactive</string>
                </dict>
                </plist>
                """);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("LoginItem.SetEnabled", ex);
        }
    }
}

/// <summary>One copy per profile at a time: a second widget would double the
/// calls to Anthropic's rate-limited usage endpoint. An exclusive lock on a
/// file in the profile's data folder, released by the OS when the process
/// exits. A second launch touches <see cref="SignalPath"/> instead, which the
/// running copy watches to open its flyout.</summary>
internal static class SingleInstance
{
    private static FileStream? _lock;

    public static string SignalPath => Path.Combine(AppPaths.DataDirectory, "app.lock.signal");

    public static bool TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            _lock = new FileStream(Path.Combine(AppPaths.DataDirectory, "app.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
