using System.Diagnostics;
using System.IO;
using System.Text;

namespace StickyAIusage.Services;

/// <summary>Where Claude Code keeps its sign-in JSON
/// (<c>{"claudeAiOauth": {accessToken, expiresAt, ...}}</c>). Read only: the app
/// never writes it (<see cref="ClaudeCredentials"/>).</summary>
public interface ICredentialStore
{
    /// <summary>Short description for the log, e.g. "macOS Keychain".</summary>
    string Description { get; }

    /// <summary>The raw JSON, or null if there is none. Never throws.</summary>
    string? Read();
}

/// <summary>Picks the store Claude Code uses on this OS: on macOS the
/// login Keychain item "Claude Code-credentials", falling back to the file;
/// elsewhere %USERPROFILE%\.claude\.credentials.json.</summary>
public static class ClaudeCredentialsReader
{
    public static string CredentialsPath => Path.Combine(AppPaths.ClaudeDirectory, ".credentials.json");

    public static ICredentialStore ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new MacCredentialStore() : new FileCredentialStore(CredentialsPath);
}

/// <summary>The plain JSON file Claude Code uses on Windows and Linux.</summary>
public sealed class FileCredentialStore(string path) : ICredentialStore
{
    public string Path { get; } = path;
    public string Description => "credentials file";

    public string? Read()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            // FileShare.ReadWrite: Claude Code may have the file open.
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("FileCredentialStore.Read", ex);
            return null;
        }
    }
}

/// <summary>macOS: the Keychain item when there is one, else the file. One read is one
/// <c>security</c> process at most, so a fetch reads the sign-in once.</summary>
public sealed class MacCredentialStore(ICredentialStore? keychain = null, ICredentialStore? file = null) : ICredentialStore
{
    private readonly ICredentialStore _keychain = keychain ?? new MacKeychainCredentialStore();
    private readonly ICredentialStore _file = file ?? new FileCredentialStore(ClaudeCredentialsReader.CredentialsPath);
    private string _description = "macOS Keychain";

    public string Description => _description;

    public string? Read()
    {
        if (_keychain.Read() is { } json)
        {
            _description = _keychain.Description;
            return json;
        }
        _description = _file.Description;
        return _file.Read();
    }
}

/// <summary>
/// The login Keychain generic-password item Claude Code uses on macOS, read through
/// Apple's /usr/bin/security tool rather than the Security framework: the item's
/// access list already trusts that tool (Claude Code writes the item with it), so
/// reading it doesn't prompt. Read only.
/// </summary>
public sealed class MacKeychainCredentialStore(string service = MacKeychainCredentialStore.DefaultService) : ICredentialStore
{
    public const string DefaultService = "Claude Code-credentials";
    private const string SecurityTool = "/usr/bin/security";

    public string Service { get; } = service;
    public string Description => "macOS Keychain";

    public string? Read()
    {
        var (exitCode, stdout) = Run(["find-generic-password", "-s", Service, "-w"]);
        var secret = stdout.Trim();
        return exitCode == 0 && secret.Length > 0 ? secret : null;
    }

    private static (int ExitCode, string Stdout) Run(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(SecurityTool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { /* already gone */ }
                return (-1, "");
            }
            _ = stderr.Result;
            return (process.ExitCode, stdout.Result);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacKeychainCredentialStore.Run", ex);
            return (-1, "");
        }
    }
}
