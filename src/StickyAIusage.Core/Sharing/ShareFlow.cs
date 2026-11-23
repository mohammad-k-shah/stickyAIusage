using StickyAIusage.Alerts;
using StickyAIusage.Services;

namespace StickyAIusage.Sharing;

/// <summary>What the Share menu offers.</summary>
public enum ShareAction { Copy, Save, PostX, PostLinkedIn, PostBluesky }

/// <summary>Each app's clipboard, save dialog and browser. Never throws: a
/// failure is false (or null) and is logged by the app.</summary>
public interface IShareOutput
{
    /// <summary>Puts the PNG on the clipboard as an image; false when it couldn't.</summary>
    Task<bool> CopyImageAsync(byte[] png);

    /// <summary>Asks where to save (Pictures, the suggested name); the path saved to, or null when cancelled.</summary>
    Task<string?> SaveImageAsync(byte[] png, string suggestedName);

    /// <summary>Saves without asking, in Pictures; the path, or null when it couldn't.</summary>
    Task<string?> SaveToPicturesAsync(byte[] png, string fileName);

    /// <summary>Opens the page in the default browser; false when it couldn't.</summary>
    Task<bool> OpenUrlAsync(string url);
}

/// <summary>
/// Runs a Share menu choice and says what happened, the same way in both apps.
/// A clipboard that refuses the image (locked-down machines, a macOS without
/// permission for the script runner) never loses the card: it is saved to
/// Pictures instead and the message says where.
/// </summary>
public static class ShareFlow
{
    public const string Copied = "Image copied. Paste it into your post.";
    public const string NothingWorked = "Couldn't copy or save the image. The log has the details.";

    /// <returns>The line to show under the flyout's header; null when the user cancelled.</returns>
    public static async Task<string?> RunAsync(ShareAction action, byte[] png, ShareCardVariant variant, IShareOutput output,
        DateTimeOffset now, TimeZoneInfo? zone = null, int? year = null)
    {
        var fileName = ShareCards.FileName(now, zone, year);
        try
        {
            if (action == ShareAction.Save)
                return await output.SaveImageAsync(png, fileName) is { } saved ? "Saved " + Path.GetFileName(saved) + "." : null;

            if (!await output.CopyImageAsync(png))
            {
                DiagnosticLog.WriteInfo("Share: the clipboard refused the image; saving it to Pictures instead.");
                var fallback = await output.SaveToPicturesAsync(png, fileName);
                if (fallback is null)
                    return NothingWorked;
                if (action == ShareAction.Copy)
                    return $"Couldn't copy the image, so it was saved instead: {fallback}";
                var site = Site(action);
                return await output.OpenUrlAsync(ShareCards.PostUrl(Target(action), variant, year))
                    ? $"Couldn't copy the image, so it was saved to {fallback}. Attach it to the {site} post that just opened."
                    : $"Couldn't copy the image or open {site}; it was saved to {fallback}.";
            }

            if (action == ShareAction.Copy)
                return Copied;
            var name = Site(action);
            return await output.OpenUrlAsync(ShareCards.PostUrl(Target(action), variant, year))
                ? $"Image copied. Paste it into the {name} post that just opened."
                : $"Image copied, but {name} didn't open in your browser.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ShareFlow.RunAsync", ex);
            return NothingWorked;
        }
    }

    /// <summary>"Post on X", "Post on LinkedIn", "Post on Bluesky".</summary>
    public static string MenuText(ShareAction action) => action switch
    {
        ShareAction.Copy => "Copy image",
        ShareAction.Save => "Save image…",
        _ => "Post on " + Site(action)
    };

    public static IReadOnlyList<ShareAction> Actions { get; } =
        [ShareAction.Copy, ShareAction.Save, ShareAction.PostX, ShareAction.PostLinkedIn, ShareAction.PostBluesky];

    private static ShareTarget Target(ShareAction action) => action switch
    {
        ShareAction.PostX => ShareTarget.X,
        ShareAction.PostBluesky => ShareTarget.Bluesky,
        _ => ShareTarget.LinkedIn
    };

    private static string Site(ShareAction action) => action switch
    {
        ShareAction.PostX => "X",
        ShareAction.PostBluesky => "Bluesky",
        _ => "LinkedIn"
    };

    /// <summary>The osascript arguments that put a PNG file on the macOS clipboard as an image.</summary>
    public static IReadOnlyList<string> MacClipboardArguments(string pngPath) =>
        ["-e", $"set the clipboard to (read (POSIX file {AppleScriptNotification.Literal(pngPath)}) as «class PNGf»)"];

    /// <summary>The tooltip of a Share button that can't share yet.</summary>
    public const string NoReadingTooltip = "Share needs a usage reading first";

    public const string ShareTooltip = "Share an image of today's usage";
    public const string ShareSessionTooltip = "Share an image of this session";
    public const string ShareYearTooltip = "Share an image of your year with Claude Code";

    /// <summary>The header's Share tooltip: today with a reading, else the year card in season, else why it is off.</summary>
    public static string HeaderTooltip(bool hasReading, bool yearInSeason) =>
        hasReading ? ShareTooltip : yearInSeason ? ShareYearTooltip : NoReadingTooltip;
}
