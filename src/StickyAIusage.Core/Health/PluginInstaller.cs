using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace StickyAIusage.Health;

/// <summary>How an install attempt ended.</summary>
public enum InstallOutcome
{
    Installed,
    AlreadyInstalled,

    /// <summary>The marketplace has no such plugin (or the marketplace can't be read).</summary>
    NotFound,

    /// <summary>An organization setting blocks it.</summary>
    Policy,

    /// <summary>GitHub couldn't be reached.</summary>
    Offline,

    /// <summary>Claude Code's command line isn't on this computer.</summary>
    NoCli,

    Failed
}

/// <param name="Message">One sentence for the window (Claude Code's own when it gave one).</param>
/// <param name="Transcript">The commands and everything they printed, for the output box.</param>
public sealed record InstallResult(InstallOutcome Outcome, string Message, string Transcript)
{
    public bool Succeeded => Outcome is InstallOutcome.Installed or InstallOutcome.AlreadyInstalled;
}

/// <summary>One finished run of a program.</summary>
public sealed record CliRun(int ExitCode, string Output, string Error, bool TimedOut = false);

/// <summary>Starts Claude Code's command line; tests pass a fake, so no test
/// ever starts a process.</summary>
public interface IClaudeCli
{
    Task<CliRun> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Connect → "Run it for me": installs the companion plugin with Claude Code's
/// own command line, never anything else. The command is shown before it
/// runs; it makes no model call (a plugin install doesn't). Command building,
/// finding <c>claude</c> and reading its output are pure and tested; the
/// process start sits behind <see cref="IClaudeCli"/>.
/// </summary>
public static class PluginInstaller
{
    public const string PluginName = "sticky-usage";

    /// <summary>The marketplace's name in <c>.claude-plugin/marketplace.json</c>.</summary>
    public const string MarketplaceName = "sticky-ai-usage";

    public const string PluginId = PluginName + "@" + MarketplaceName;

    /// <summary>Where Claude Code finds the marketplace: the public repository.</summary>
    public const string DefaultSource = "mohammad-k-shah/stickyAIusage";

    /// <summary>A developer's override of <see cref="DefaultSource"/> (a local
    /// checkout, a fork): set it to try Connect against your own copy.</summary>
    public const string SourceVariable = "STICKYAIUSAGE_PLUGIN_MARKETPLACE";

    /// <summary>The shell's one-command form (<c>--marketplace</c>); older builds
    /// get the two-step form. Checked on code.claude.com, 2026-11-20.</summary>
    public static readonly Version OneStepMinimum = new(2, 1, 292);

    /// <summary>The oldest build whose <c>--json</c> result line was checked (2.1.318, 2026-11-20).</summary>
    public static readonly Version JsonMinimum = new(2, 1, 318);

    /// <summary>The slash command's <c>--marketplace</c> form.</summary>
    public static readonly Version SessionCommandMinimum = new(2, 1, 275);

    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The marketplace source to install from.</summary>
    public static string Source(Func<string, string?> environment) =>
        environment(SourceVariable) is { Length: > 0 } custom ? custom.Trim() : DefaultSource;

    /// <summary>What Settings and Connect show and copy for a terminal.</summary>
    public static string ShellCommand(string source) => Display(["plugin", "install", PluginName, "--marketplace", source]);

    /// <summary>What to paste into a Claude Code session or the Desktop Code tab.</summary>
    public static string SessionCommand(string source) => $"/plugin install {PluginName} --marketplace {source}";

    /// <summary>The argument lists to run, in order: one command from
    /// <see cref="OneStepMinimum"/> on (with <c>--json</c> where it was
    /// checked), else adding the marketplace and then installing from it. An
    /// unknown version gets the two-step form, which every plugin-capable build knows.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Steps(Version? cliVersion, string source)
    {
        if (cliVersion is { } version && version >= OneStepMinimum)
        {
            List<string> install = ["plugin", "install", PluginName, "--marketplace", source];
            if (version >= JsonMinimum)
                install.Add("--json");
            return [install];
        }
        return [["plugin", "marketplace", "add", source], ["plugin", "install", PluginId]];
    }

    /// <summary>"claude plugin install sticky-usage --marketplace …", quoted where needed.</summary>
    public static string Display(IReadOnlyList<string> arguments) =>
        "claude " + string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')')
            ? argument
            : "\"" + argument.Replace("\"", "\\\"") + "\"";

    /// <summary>The version <c>claude --version</c> printed ("2.1.318 (Claude Code)").</summary>
    public static Version? ParseVersion(string output)
    {
        foreach (var line in output.Split('\n'))
            if (HealthCheck.ParseVersion(line) is { } version)
                return version;
        return null;
    }

    /// <summary>Where <c>claude</c> is: on PATH first, then where its installers
    /// put it (a Mac app started from Finder has a short PATH). Null: not found.</summary>
    /// <param name="home">The user's home folder.</param>
    public static string? FindClaude(Func<string, string?> environment, Func<string, bool> fileExists, bool isWindows, string home)
    {
        string[] names = isWindows ? ["claude.exe", "claude.cmd"] : ["claude"];
        var separator = isWindows ? ';' : ':';
        var folders = (environment("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Trim('"')).ToList();
        if (isWindows)
        {
            folders.Add(Join(home, ".local", "bin"));
            if (environment("APPDATA") is { Length: > 0 } appData)
                folders.Add(Join(appData, "npm"));
        }
        else
        {
            folders.AddRange([Join(home, ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin",
                Join(home, ".npm-global", "bin"), Join(home, ".claude", "local")]);
        }
        foreach (var folder in folders)
            foreach (var name in names)
            {
                var path = Join(folder, name);
                if (fileExists(path))
                    return path;
            }
        return null;

        // The target OS's separator, whatever this one is (the Mac rules are tested on Windows too).
        string Join(params string[] parts) =>
            string.Join(isWindows ? '\\' : '/', parts.Select((p, i) => i == 0 ? p.TrimEnd('\\', '/') : p.Trim('\\', '/')));
    }

    /// <summary>What one install command's output means. Claude Code's
    /// <c>--json</c> line wins when there is one; otherwise its words and the exit code.</summary>
    public static (InstallOutcome Outcome, string Message) Classify(CliRun run)
    {
        if (run.TimedOut)
            return (InstallOutcome.Failed, "Claude Code didn't finish within three minutes.");
        if (JsonResult(run.Output) is { } json)
        {
            var message = json.Message ?? (json.Ok ? "Installed." : "The install failed.");
            if (json.Ok)
                return (IsAlready(message) ? InstallOutcome.AlreadyInstalled : InstallOutcome.Installed, FirstLine(message));
            return (FailureKind(json.FailureCode + " " + message), FirstLine(message));
        }

        var all = (run.Output + "\n" + run.Error).Trim();
        if (run.ExitCode == 0)
            return (IsAlready(all) ? InstallOutcome.AlreadyInstalled : InstallOutcome.Installed,
                IsAlready(all) ? "The plugin is already installed." : "Installed.");
        var line = all.Split('\n').Select(l => l.Trim().TrimStart('✘', '✗', ' ')).LastOrDefault(l => l.Length > 0)
                   ?? $"Claude Code ended with exit code {run.ExitCode}.";
        return (FailureKind(all), line);
    }

    private static bool IsAlready(string text) => text.Contains("already installed", StringComparison.OrdinalIgnoreCase);

    private static InstallOutcome FailureKind(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("polic") || t.Contains("blocked") || t.Contains("not allowed") || t.Contains("managed setting") ||
            t.Contains("strictknownmarketplaces") || t.Contains("allowedmarketplaces") || t.Contains("administrator"))
            return InstallOutcome.Policy;
        if (t.Contains("not_found") || t.Contains("not found in marketplace") || t.Contains("marketplace file not found") ||
            t.Contains("no marketplace") || t.Contains("could not find marketplace"))
            return InstallOutcome.NotFound;
        if (t.Contains("enotfound") || t.Contains("getaddrinfo") || t.Contains("could not resolve") || t.Contains("network") ||
            t.Contains("etimedout") || t.Contains("econnrefused") || t.Contains("econnreset") || t.Contains("unable to access") ||
            t.Contains("offline"))
            return InstallOutcome.Offline;
        return InstallOutcome.Failed;
    }

    private sealed record JsonLine(bool Ok, string? Message, string? FailureCode);

    /// <summary>The last line of stdout that is a JSON object with an "outcome".</summary>
    private static JsonLine? JsonResult(string output)
    {
        foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("outcome", out var outcome))
                    continue;
                return new JsonLine(outcome.ValueKind == JsonValueKind.String && outcome.GetString() == "ok",
                    Text(root, "message"), Text(root, "failureCode"));
            }
            catch (JsonException)
            {
            }
        }
        return null;

        static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    /// <summary>Runs the install: <c>claude --version</c>, then the steps for
    /// that version. Never throws; every outcome comes back with what ran and
    /// what it printed.</summary>
    /// <param name="claude">The executable <see cref="FindClaude"/> found; null: none.</param>
    public static async Task<InstallResult> InstallAsync(IClaudeCli cli, string? claude, string source, CancellationToken cancellationToken = default)
    {
        if (claude is null)
            return new InstallResult(InstallOutcome.NoCli,
                "Claude Code's command line wasn't found on this computer. Copy the command for Claude Code or the Desktop Code tab instead.",
                "");

        var transcript = new StringBuilder();
        try
        {
            var versionRun = await cli.RunAsync(claude, ["--version"], VersionTimeout, cancellationToken);
            Append(transcript, ["--version"], versionRun);
            var version = versionRun.ExitCode == 0 ? ParseVersion(versionRun.Output) : null;

            (InstallOutcome Outcome, string Message) last = (InstallOutcome.Failed, "Nothing ran.");
            foreach (var step in Steps(version, source))
            {
                var run = await cli.RunAsync(claude, step, InstallTimeout, cancellationToken);
                Append(transcript, step, run);
                last = Classify(run);
                // Adding a marketplace that is already there isn't a failure of the install.
                var isAdd = step.Count > 1 && step[1] == "marketplace";
                if (isAdd && (run.ExitCode == 0 || (run.Output + run.Error).Contains("already", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!last.Outcome.IsSuccess())
                    break;
            }
            Services.DiagnosticLog.WriteInfo($"Connect: plugin install {last.Outcome}: {last.Message}");
            return new InstallResult(last.Outcome, last.Message, transcript.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Services.DiagnosticLog.WriteError("PluginInstaller.InstallAsync", ex);
            return new InstallResult(InstallOutcome.Failed, $"Couldn't start Claude Code: {ex.Message}", transcript.ToString().TrimEnd());
        }
    }

    private static bool IsSuccess(this InstallOutcome outcome) =>
        outcome is InstallOutcome.Installed or InstallOutcome.AlreadyInstalled;

    private static void Append(StringBuilder transcript, IReadOnlyList<string> arguments, CliRun run)
    {
        transcript.AppendLine("> " + Display(arguments));
        if (run.Output.Trim().Length > 0)
            transcript.AppendLine(run.Output.Trim());
        if (run.Error.Trim().Length > 0)
            transcript.AppendLine(run.Error.Trim());
        transcript.AppendLine(run.TimedOut ? "(stopped: no answer within the time limit)" : $"(exit code {run.ExitCode})");
    }
}

/// <summary>Starts the real <c>claude</c>: no window, no input, output captured.
/// A <c>.cmd</c> (npm's shim) runs through <c>cmd /c</c>, as Windows needs.</summary>
public sealed class ClaudeCliProcess : IClaudeCli
{
    public async Task<CliRun> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows() && Path.GetExtension(executable) is var extension &&
            (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";
            // /s: cmd strips the outer quotes and keeps the rest as written.
            start.Arguments = "/d /s /c \"" + string.Join(' ', [QuoteForCmd(executable), .. arguments.Select(QuoteForCmd)]) + "\"";
        }
        else
        {
            start.FileName = executable;
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
        }
        // A marketplace is a git clone: it must never stop to ask for a password.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new CliRun(-1, await Finished(output), await Finished(error), TimedOut: true);
        }
        return new CliRun(process.ExitCode, await output, await error);
    }

    private static async Task<string> Finished(Task<string> read)
    {
        try { return await read.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { return ""; }
    }

    private static string QuoteForCmd(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')' or '%')
            ? argument
            : "\"" + argument.Replace("\"", "\"\"") + "\"";
}
