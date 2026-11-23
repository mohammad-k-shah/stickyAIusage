using StickyAIusage.Services;

namespace StickyAIusage.ViewModels;

/// <summary>Commands the widget offers to copy; it never runs them itself
/// (Windows' Sign in button opens a terminal with the first one).</summary>
public static class ClaudeCommands
{
    /// <summary>Claude Code's own sign-in.</summary>
    public const string SignIn = "claude auth login";

    /// <summary>Installs the companion plugin from this repository's marketplace.</summary>
    public const string InstallPlugin = "claude plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage";
}

/// <param name="Label">The segment's text: "Plugin".</param>
/// <param name="Description">One or two sentences under the segments.</param>
/// <param name="Warning">Shown for the modes that use the unofficial usage
/// endpoint; null for the others.</param>
public sealed record UsageSourceOption(ClaudeSourceMode Mode, string Label, string Description, string? Warning);

/// <summary>The choices of Settings → Usage source, in display order, with
/// the words both Settings windows show for them.</summary>
public static class UsageSourceOptions
{
    /// <summary>What the two modes that read the sign-in say about it.</summary>
    public const string UnofficialWarning =
        "Unofficial: reads the sign-in Claude Code saved on this computer and asks Anthropic's usage service, which " +
        "Anthropic hasn't documented for other apps. The sign-in is only read, never renewed or changed: when it " +
        "expires, the numbers wait until Claude Code runs again.";

    public static IReadOnlyList<UsageSourceOption> All { get; } =
    [
        new(ClaudeSourceMode.Feed, "Plugin",
            "Official. Your limits as Claude Code reports them, through the Claude Code plugin after each turn. " +
            "No network requests, and your sign-in is never read.", null),
        new(ClaudeSourceMode.Auto, "Automatic",
            "The plugin while it has current numbers; otherwise Claude's usage service.", UnofficialWarning),
        new(ClaudeSourceMode.Legacy, "Legacy",
            "Claude's usage service only, as before the plugin. It also has monthly spend caps in dollars and " +
            "credits, which the plugin doesn't carry.", UnofficialWarning),
        new(ClaudeSourceMode.Off, "Off",
            "No plan limits. The session cards and their costs still show.", null)
    ];

    public static UsageSourceOption For(ClaudeSourceMode mode) => All.First(o => o.Mode == mode);

    /// <summary>The line about installing the plugin: only for the modes
    /// that read it, and only while the plugin hasn't connected yet.</summary>
    public static bool ShowsPluginInstall(ClaudeSourceMode mode, bool pluginConnected = false) =>
        !pluginConnected && mode is ClaudeSourceMode.Feed or ClaudeSourceMode.Auto;
}

public enum FlyoutNoticeKind
{
    /// <summary>The sign-in expired: how to sign in again.</summary>
    SignIn,

    /// <summary>The plugin's feed has nothing yet: how to install it.</summary>
    InstallPlugin,

    /// <summary>The numbers come from the usage endpoint: the plugin can
    /// give them without the sign-in. Dismissed for good with one click.</summary>
    PluginTip
}

/// <summary>A short note at the top of the flyout with one command to copy.</summary>
/// <param name="CopyLabel">The copy button's text.</param>
/// <param name="CopyText">What it puts on the clipboard.</param>
public sealed record FlyoutNotice(FlyoutNoticeKind Kind, string Message, string CopyLabel, string CopyText, bool CanDismiss)
{
    /// <summary>The notice is about the plugin: it also offers the Connect step
    /// (<see cref="ConnectLabel"/>), which can install it with one click.</summary>
    public bool CanConnect => Kind is FlyoutNoticeKind.InstallPlugin or FlyoutNoticeKind.PluginTip;

    public const string ConnectLabel = "Connect…";

    public const string SignInMessage =
        "Your Claude sign-in has expired. Sign in again with Claude Code: run claude auth login in a terminal, " +
        "or /login inside Claude Code. The widget picks it up by itself.";

    public const string InstallPluginMessage =
        "No numbers from Claude Code yet. Install the Sticky AI Usage plugin, then send Claude Code one message. " +
        "For a monthly spend cap, choose Legacy in Settings → Usage source.";

    public const string PluginTipMessage =
        "Tip: these numbers come from Claude's usage service through your Claude Code sign-in. The Claude Code " +
        "plugin can give them from Claude Code itself, without the sign-in.";

    public static FlyoutNotice SignIn { get; } =
        new(FlyoutNoticeKind.SignIn, SignInMessage, "Copy command", ClaudeCommands.SignIn, CanDismiss: false);

    public static FlyoutNotice InstallPlugin { get; } =
        new(FlyoutNoticeKind.InstallPlugin, InstallPluginMessage, "Copy install command", ClaudeCommands.InstallPlugin, CanDismiss: false);

    public static FlyoutNotice PluginTip { get; } =
        new(FlyoutNoticeKind.PluginTip, PluginTipMessage, "Copy install command", ClaudeCommands.InstallPlugin, CanDismiss: true);
}
