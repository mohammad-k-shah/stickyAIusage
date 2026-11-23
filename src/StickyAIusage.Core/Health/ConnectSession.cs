using System.IO;
using StickyAIusage.Models;

namespace StickyAIusage.Health;

/// <summary>
/// One Connect step on screen (the first-run window, or Connect from Health):
/// what was done there and what the feed shows now, as a <see cref="ConnectView"/>.
/// Both apps' windows bind to it; <see cref="Changed"/> asks them to repaint
/// (always raised on the caller's thread: the window's own calls and its
/// reading updates).
/// </summary>
public sealed class ConnectSession
{
    private readonly Func<UsageSnapshot?> _usage;
    private readonly Func<bool> _pluginWrote;
    private readonly IClaudeCli _cli;
    private bool _installing;
    private bool _copied;
    private InstallResult? _result;

    /// <param name="usage">The window's latest reading.</param>
    /// <param name="pluginWrote">The feed folder has the plugin's marker or a record.</param>
    /// <param name="claudePath">Where <c>claude</c> is (<see cref="PluginInstaller.FindClaude"/>); null: not found.</param>
    public ConnectSession(Func<UsageSnapshot?> usage, Func<bool> pluginWrote, IClaudeCli cli, string? claudePath, string source)
    {
        _usage = usage;
        _pluginWrote = pluginWrote;
        _cli = cli;
        ClaudePath = claudePath;
        Source = source;
    }

    /// <summary>The real thing: <c>claude</c> found on this computer, the marketplace from the environment.</summary>
    public static ConnectSession ForThisComputer(Func<UsageSnapshot?> usage, Func<bool> pluginWrote) =>
        new(usage, pluginWrote, new ClaudeCliProcess(),
            PluginInstaller.FindClaude(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            PluginInstaller.Source(Environment.GetEnvironmentVariable));

    public string? ClaudePath { get; }
    public string Source { get; }
    public bool HasCli => ClaudePath is not null;
    public string ShellCommand => PluginInstaller.ShellCommand(Source);
    public string SessionCommand => PluginInstaller.SessionCommand(Source);

    public ConnectView View => ConnectFlow.View(_installing, _result, _copied, _usage(), PluginWroteSafely(), ShellCommand, SessionCommand);

    public event Action? Changed;

    /// <summary>"Run it for me". A second click while one runs does nothing.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_installing)
            return;
        _installing = true;
        _result = null;
        Changed?.Invoke();
        try
        {
            _result = await PluginInstaller.InstallAsync(_cli, ClaudePath, Source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _result = null;
        }
        finally
        {
            _installing = false;
            Changed?.Invoke();
        }
    }

    /// <summary>"Copy for Claude Code" put <see cref="SessionCommand"/> on the clipboard.</summary>
    public void MarkCopied()
    {
        _copied = true;
        Changed?.Invoke();
    }

    /// <summary>The window has a new reading: the view may have moved on.</summary>
    public void Refresh() => Changed?.Invoke();

    private bool PluginWroteSafely()
    {
        try { return _pluginWrote(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
