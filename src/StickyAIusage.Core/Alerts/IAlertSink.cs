namespace StickyAIusage.Alerts;

/// <summary>Shows a notification: the tray balloon on Windows (a toast on
/// Windows 11), a banner on macOS. Called on the UI thread; never throws.</summary>
public interface IAlertSink
{
    void Show(IReadOnlyList<Alert> alerts);
}
