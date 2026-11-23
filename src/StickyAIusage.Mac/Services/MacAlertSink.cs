using System.Diagnostics;
using StickyAIusage.Alerts;
using StickyAIusage.Services;

namespace StickyAIusage.Mac.Services;

/// <summary>
/// macOS alerts: a banner through <c>/usr/bin/osascript</c> and Standard
/// Additions' <c>display notification</c>. The texts are passed as arguments
/// (never through a shell) and escaped for AppleScript
/// (<see cref="AppleScriptNotification"/>). macOS attributes the banner to
/// Script Editor, which needs notification permission once (Settings says
/// so); a native notification needs a signed app (later). A refresh's alerts
/// go as one banner (<see cref="AlertMessages"/>). Never waits for osascript.
/// </summary>
internal sealed class MacAlertSink(string? instanceLabel) : IAlertSink
{
    private const string Osascript = "/usr/bin/osascript";

    public void Show(IReadOnlyList<Alert> alerts)
    {
        if (alerts.Count == 0)
            return;
        try
        {
            var (title, body) = AlertMessages.ForNotification(alerts, instanceLabel);
            var start = new ProcessStartInfo(Osascript)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in AppleScriptNotification.Arguments(title, body))
                start.ArgumentList.Add(argument);
            var process = Process.Start(start);
            if (process is null)
                return;
            // Read what it says so a refused banner leaves a log line, then let it go.
            _ = Task.Run(async () =>
            {
                using (process)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                        DiagnosticLog.WriteInfo($"Alert banner: osascript exited {process.ExitCode}: {error.Trim()}");
                }
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacAlertSink.Show", ex);
        }
    }
}
