using System.IO;

namespace StickyAIusage.Updates;

/// <summary>How this copy got onto the computer, so an update notice can name the
/// command that updates it.</summary>
public enum InstallMethod
{
    /// <summary>A zip unpacked by hand, a build from source, or not known: the release page.</summary>
    Download,

    /// <summary><c>StickyAIusage-Setup-x64.exe</c> run by hand: its new version is on the release page.</summary>
    Setup,

    /// <summary>winget (the same Setup, started with <c>/CHANNEL=winget</c>).</summary>
    Winget,

    /// <summary>A Scoop bucket (the app sits under <c>scoop\apps\</c>).</summary>
    Scoop,

    /// <summary><c>scripts/install.ps1</c> or, on a Mac, <c>scripts/install.sh</c>.</summary>
    Script
}

/// <summary>
/// Finds the <see cref="InstallMethod"/> and the command that updates it. On
/// Windows each installer leaves a one-word <c>install-method</c> file beside the
/// exe (the Setup: "setup" or "winget"; <c>install.ps1</c>: "script"); Scoop is
/// known by its folder. On a Mac nothing is written into the app (that would break
/// its signature): the install script is the way to update any copy there, since
/// it replaces the app in Applications.
/// </summary>
public static class InstallMethods
{
    public const string FileName = "install-method";

    public const string WingetId = "MohammadKShah.StickyAIUsage";
    public const string ScoopName = "sticky-ai-usage";

    private const string ScriptBase = "https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/";

    /// <param name="appDirectory">The folder the running exe is in.</param>
    /// <param name="readFile">Reads a small file (tests hand in their own); null when it can't.</param>
    public static InstallMethod Detect(string appDirectory, bool isMac, Func<string, string?>? readFile = null)
    {
        if (isMac)
            return InstallMethod.Script;
        var folder = appDirectory.Replace('/', '\\');
        if (folder.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase))
            return InstallMethod.Scoop;
        readFile ??= ReadSmallFile;
        var word = readFile(Path.Combine(appDirectory, FileName))?.Trim().ToLowerInvariant();
        return word switch
        {
            "winget" => InstallMethod.Winget,
            "setup" => InstallMethod.Setup,
            "script" => InstallMethod.Script,
            _ => InstallMethod.Download
        };
    }

    /// <summary>The command that installs <paramref name="preRelease"/>-or-not newest
    /// version for this method; null when there is none (download it from the release page).</summary>
    public static string? UpdateCommand(InstallMethod method, bool isMac, bool preRelease) => method switch
    {
        InstallMethod.Winget => $"winget upgrade {WingetId}",
        InstallMethod.Scoop => $"scoop update {ScoopName}",
        InstallMethod.Script when isMac => preRelease
            ? $"curl -fsSL {ScriptBase}install.sh | sh -s -- --prerelease"
            : $"curl -fsSL {ScriptBase}install.sh | sh",
        InstallMethod.Script => preRelease
            ? $"& ([scriptblock]::Create((irm {ScriptBase}install.ps1))) -Prerelease"
            : $"irm {ScriptBase}install.ps1 | iex",
        _ => null
    };

    /// <summary>Where the command goes ("Run it in PowerShell"); null without a command.</summary>
    public static string? CommandPlace(InstallMethod method, bool isMac) =>
        UpdateCommand(method, isMac, preRelease: false) is null ? null : isMac ? "Terminal" : "PowerShell";

    private static string? ReadSmallFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 64 ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
