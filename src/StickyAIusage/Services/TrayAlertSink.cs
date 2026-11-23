using Hardcodet.Wpf.TaskbarNotification;
using StickyAIusage.Alerts;

namespace StickyAIusage.Services;

/// <summary>
/// Windows alerts: a balloon from the tray icon the app already has, which
/// Windows 10 and 11 show as a toast (and keep in the notification centre).
/// No new dependency and no app identity needed. Windows shows one balloon at
/// a time and queues the rest, so a refresh's alerts go as one
/// (<see cref="AlertMessages"/>). Clicking it raises the icon's
/// <c>TrayBalloonTipClicked</c>, which opens the flyout (<see cref="App"/>).
/// </summary>
internal sealed class TrayAlertSink(TaskbarIcon icon, string? instanceLabel) : IAlertSink
{
    private System.Drawing.Icon? _appIcon;

    public void Show(IReadOnlyList<Alert> alerts)
    {
        if (alerts.Count == 0)
            return;
        try
        {
            var (title, body) = AlertMessages.ForNotification(alerts, instanceLabel);
            // The app's own icon, large: the toast shows it beside the text.
            _appIcon ??= App.LoadTrayIcon(large: true);
            icon.ShowBalloonTip(title, body, _appIcon, largeIcon: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("TrayAlertSink.Show", ex);
        }
    }
}
