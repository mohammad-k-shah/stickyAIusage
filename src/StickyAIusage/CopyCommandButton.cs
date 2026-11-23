using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;
using StickyAIusage.Services;

namespace StickyAIusage;

/// <summary>The "Copy command" buttons: put the command on the clipboard and
/// say "Copied" on the button for two seconds.</summary>
internal static class CopyCommandButton
{
    private static readonly TimeSpan ConfirmFor = TimeSpan.FromSeconds(2);

    /// <summary>The label to restore and the timer that restores it, while
    /// a button says "Copied".</summary>
    private sealed record Confirmation(object Label, DispatcherTimer Timer);

    private static readonly ConditionalWeakTable<Button, Confirmation> Confirming = new();

    public static void Copy(Button button, string command)
    {
        try
        {
            // copy: true keeps the text on the clipboard after the app exits.
            System.Windows.Clipboard.SetDataObject(command, copy: true);
            Confirm(button, "Copied");
        }
        catch (Exception ex)
        {
            // Another app can hold the clipboard open; say so rather than "Copied".
            DiagnosticLog.WriteError("CopyCommandButton.Copy", ex);
            Confirm(button, "Couldn't copy");
        }
    }

    private static void Confirm(Button button, string text)
    {
        if (Confirming.TryGetValue(button, out var running))
        {
            running.Timer.Stop();
            Confirming.Remove(button);
        }
        var label = running?.Label ?? button.Content;

        var timer = new DispatcherTimer { Interval = ConfirmFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // SetLabel may have changed the label to go back to meanwhile.
            button.Content = Confirming.TryGetValue(button, out var current) ? current.Label : label;
            Confirming.Remove(button);
        };
        Confirming.Add(button, new Confirmation(label, timer));
        button.Content = text;
        timer.Start();
    }

    /// <summary>Sets a button's label, unless it is showing "Copied" (then the
    /// label it goes back to).</summary>
    public static void SetLabel(Button button, string label)
    {
        if (Confirming.TryGetValue(button, out var running))
            Confirming.AddOrUpdate(button, running with { Label = label });
        else
            button.Content = label;
    }
}
