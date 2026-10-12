using System.IO;
using System.Security;
using StickyAIusage.Services;

namespace StickyAIusage.Mac.Native;

/// <summary>"Launch at login" via a per-user LaunchAgent - the macOS
/// counterpart of the Windows app's HKCU Run key. No admin rights needed;
/// launchd picks it up at the next login.</summary>
internal static class LoginItem
{
    private const string Label = "com.stickyaiusage.widget";

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

            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "StickyAIusage");
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>{Label}</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{SecurityElement.Escape(exe)}</string>
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

/// <summary>One copy at a time: a second widget would double the calls to
/// Anthropic's rate-limited usage endpoint. An exclusive lock on a file in the
/// app's data folder, released by the OS when the process exits.</summary>
internal static class SingleInstance
{
    private static FileStream? _lock;

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
