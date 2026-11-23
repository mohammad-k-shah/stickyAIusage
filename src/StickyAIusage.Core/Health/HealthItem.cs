namespace StickyAIusage.Health;

/// <summary>How a health check came out.</summary>
public enum HealthStatus
{
    /// <summary>Working as it should.</summary>
    Ok,

    /// <summary>Works, but something needs a look (the detail says what to do).</summary>
    Warn,

    /// <summary>Not working; the item names the one thing to do.</summary>
    Fail,

    /// <summary>A fact, not a verdict (a choice the user made, a count).</summary>
    Info
}

/// <summary>What a health item's button does. Each app maps the id to its
/// own window, clipboard or browser; the Core only names it.</summary>
public static class HealthActionIds
{
    /// <summary>Opens the Connect step (install the plugin).</summary>
    public const string Connect = "connect";

    /// <summary>Settings → Send a test alert.</summary>
    public const string SendTestAlert = "send-test-alert";

    /// <summary>Turns start at sign-in on or off, at once.</summary>
    public const string ToggleStartup = "toggle-startup";

    /// <summary>Opens the folder with diagnostics.log.</summary>
    public const string OpenLogFolder = "open-log-folder";

    /// <summary>Copies <see cref="HealthCheck.SignInCommand"/>.</summary>
    public const string CopySignIn = "copy-sign-in";

    /// <summary>Copies <see cref="HealthCheck.UpdateClaudeCodeCommand"/>.</summary>
    public const string CopyUpdateClaudeCode = "copy-update-claude-code";

    /// <summary>Copies <see cref="HealthCheck.UpdatePluginCommand"/>.</summary>
    public const string CopyUpdatePlugin = "copy-update-plugin";

    /// <summary>Opens <see cref="HealthCheck.ClaudeCodeSetupUrl"/> in the browser.</summary>
    public const string GetClaudeCode = "get-claude-code";

    /// <summary>Shows Settings → Usage source.</summary>
    public const string OpenUsageSource = "open-usage-source";

    /// <summary>Opens <see cref="HealthCheck.ReleasesUrl"/> in the browser.</summary>
    public const string OpenReleases = "open-releases";

    /// <summary>The text the copy actions put on the clipboard; null for the others.</summary>
    public static string? CopyText(string? actionId) => actionId switch
    {
        CopySignIn => HealthCheck.SignInCommand,
        CopyUpdateClaudeCode => HealthCheck.UpdateClaudeCodeCommand,
        CopyUpdatePlugin => HealthCheck.UpdatePluginCommand,
        _ => null
    };

    /// <summary>The page the link actions open; null for the others.</summary>
    public static string? Url(string? actionId) => actionId switch
    {
        GetClaudeCode => HealthCheck.ClaudeCodeSetupUrl,
        OpenReleases => HealthCheck.ReleasesUrl,
        _ => null
    };
}

/// <summary>One line of Settings → Health: what was checked, how it came
/// out, the details, and at most one button.</summary>
/// <param name="Id">Stable name ("claude-code", "feed", …): tests and the report use it.</param>
/// <param name="ActionId">One of <see cref="HealthActionIds"/>; null: no button.</param>
public sealed record HealthItem(string Id, string Title, HealthStatus Status, string Detail,
    string? ActionId = null, string? ActionLabel = null)
{
    /// <summary>The status as a word: the report's column and what a screen reader says.</summary>
    public string StatusLabel => Status switch
    {
        HealthStatus.Ok => "OK",
        HealthStatus.Warn => "Check",
        HealthStatus.Fail => "Action needed",
        _ => "Info"
    };

    /// <summary>A screen reader reads list items by their text.</summary>
    public override string ToString() => $"{Title}: {StatusLabel}. {Detail}";
}
