using System.Diagnostics;
using System.IO;
using System.Text;

namespace StickyAIusage.Services;

/// <summary>Where Claude Code keeps its OAuth credentials JSON
/// (<c>{"claudeAiOauth": {accessToken, refreshToken, expiresAt, ...}}</c>).
/// Read and written as a whole so the token refresher can update it in place
/// without knowing where it lives.</summary>
public interface ICredentialStore
{
    /// <summary>Short description for the log, e.g. "macOS Keychain".</summary>
    string Description { get; }

    /// <summary>The raw JSON, or null if there is none. Never throws.</summary>
    string? Read();

    /// <summary>Replaces the stored JSON. Returns false on failure. Never throws.</summary>
    bool Write(string json);
}

/// <summary>Picks the store Claude Code uses on this OS: on macOS the
/// login Keychain item "Claude Code-credentials", falling back to the file;
/// elsewhere %USERPROFILE%\.claude\.credentials.json.</summary>
public static class ClaudeCredentialsReader
{
    public static string CredentialsPath => Path.Combine(AppPaths.ClaudeDirectory, ".credentials.json");

    public static ICredentialStore ForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            var keychain = new MacKeychainCredentialStore();
            if (keychain.Read() is not null)
                return keychain;
        }
        return new FileCredentialStore(CredentialsPath);
    }
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

    /// <summary>Writes to a sibling temp file, then renames it over the
    /// original (atomic on the same volume), so a concurrent reader sees
    /// either the old file or the new one - never a truncated one. Retries
    /// briefly in case another process has the file open without share-delete.</summary>
    public bool Write(string json)
    {
        var tempPath = Path + ".stickyaiusage.tmp";
        try
        {
            // The file holds the user's tokens: owner-only, like the one Claude
            // Code writes (on Windows it inherits the .claude folder's ACL). A
            // leftover temp file is removed first, since it would keep its old mode.
            File.Delete(tempPath);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(tempPath, new UTF8Encoding(false), options))
                writer.Write(json);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, Path, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("FileCredentialStore.Write", ex);
            try { File.Delete(tempPath); } catch { /* best effort */ }
            return false;
        }
    }
}

/// <summary>
/// The login Keychain generic-password item Claude Code uses on macOS.
/// Goes through Apple's /usr/bin/security tool rather than the Security
/// framework: the item's access list already trusts that tool (Claude Code
/// writes the item with it), so reading it doesn't prompt. Writing uses
/// <c>security -i</c> with the command on stdin, so the token never appears
/// in a process argument list that other processes could see.
/// </summary>
public sealed class MacKeychainCredentialStore(string service = MacKeychainCredentialStore.DefaultService) : ICredentialStore
{
    public const string DefaultService = "Claude Code-credentials";
    private const string SecurityTool = "/usr/bin/security";

    public string Service { get; } = service;
    public string Description => "macOS Keychain";

    public string? Read()
    {
        var (exitCode, stdout, _) = Run(["find-generic-password", "-s", Service, "-w"], stdin: null);
        var secret = stdout.Trim();
        return exitCode == 0 && secret.Length > 0 ? secret : null;
    }

    public bool Write(string json)
    {
        var account = ReadAccount() ?? Environment.UserName;
        var (exitCode, stdout, stderr) = Run(["-i"], stdin: BuildUpdateCommand(account, Service, json) + "\n");
        // `security -i` exits 0 even when a command fails; the error is on stderr.
        if (exitCode == 0 && stderr.Trim().Length == 0)
            return true;
        DiagnosticLog.WriteError("MacKeychainCredentialStore.Write",
            new InvalidOperationException($"security -i exited with {exitCode}: {(stderr + stdout).Trim()}"));
        return false;
    }

    /// <summary>The item's account attribute, so the update replaces the
    /// existing item (matched on service + account) instead of adding a second one.</summary>
    private string? ReadAccount()
    {
        // Without -w the attributes are printed (stdout or stderr, depending
        // on the macOS version); the secret itself is not.
        var (exitCode, stdout, stderr) = Run(["find-generic-password", "-s", Service], stdin: null);
        return exitCode == 0 ? ParseAccount(stdout + stderr) : null;
    }

    internal static string? ParseAccount(string attributes)
    {
        const string marker = "\"acct\"<blob>=\"";
        var start = attributes.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = attributes.IndexOf('"', start);
        return end > start ? attributes[start..end] : null;
    }

    /// <summary>One line for <c>security -i</c>: -U updates in place, and -X
    /// passes the secret as hex so no quoting or escaping of the JSON is needed.</summary>
    internal static string BuildUpdateCommand(string account, string service, string json) =>
        $"add-generic-password -U -a {Quote(account)} -s {Quote(service)} -X {Convert.ToHexString(Encoding.UTF8.GetBytes(json)).ToLowerInvariant()}";

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static (int ExitCode, string Stdout, string Stderr) Run(string[] args, string? stdin)
    {
        try
        {
            var psi = new ProcessStartInfo(SecurityTool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { /* already gone */ }
                return (-1, "", "timed out");
            }
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacKeychainCredentialStore.Run", ex);
            return (-1, "", ex.Message);
        }
    }
}
