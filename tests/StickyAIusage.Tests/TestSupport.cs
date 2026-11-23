using System.Text.Json;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>Runs before any test: points the app's data folder and the log
/// at a temp folder, so no test can write to the real
/// %LOCALAPPDATA%\StickyAIusage (on a developer's machine the installed widget uses it).</summary>
internal static class TestIsolation
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stickyaiusage-tests");
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, System.IO.Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable(AppPaths.FeedDirectoryVariable, System.IO.Path.Combine(root, "feed"));
        DiagnosticLog.PathOverride = System.IO.Path.Combine(root, "diagnostics.log");
        // Times of day follow the OS format; the expected texts and the
        // screenshots are written for a 12-hour English clock.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
    }
}

/// <summary>A throwaway directory, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "stickyaiusage-tests", Guid.NewGuid().ToString("N"));

    public TempDir()
    {
        Directory.CreateDirectory(Path);
        DiagnosticLog.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stickyaiusage-tests", "diagnostics.log");
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Builds transcript lines shaped like Claude Code's .jsonl records.</summary>
public static class Transcript
{
    public const string Cwd = "/work/acme-webapp";

    public static string User(string sessionId, DateTimeOffset ts, string? effort = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["sessionId"] = sessionId,
            ["cwd"] = Cwd,
            ["version"] = "2.1.273",
            ["timestamp"] = ts.ToString("O"),
            ["effort"] = effort is null ? null : new { level = effort },
            ["message"] = new { role = "user", content = "hi" }
        });

    public static string Assistant(string sessionId, string messageId, DateTimeOffset ts,
        long input = 0, long output = 0, long cacheRead = 0, long cacheWrite = 0,
        string model = "claude-sonnet-5", bool sidechain = false) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId,
            cwd = Cwd,
            isSidechain = sidechain,
            timestamp = ts.ToString("O"),
            message = new
            {
                id = messageId,
                model,
                role = "assistant",
                stop_reason = "tool_use",
                usage = new
                {
                    input_tokens = input,
                    output_tokens = output,
                    cache_read_input_tokens = cacheRead,
                    cache_creation_input_tokens = cacheWrite
                }
            }
        });

    public static string CompactBoundary(string sessionId, DateTimeOffset ts) =>
        JsonSerializer.Serialize(new { type = "system", subtype = "compact_boundary", sessionId, timestamp = ts.ToString("O") });

    public static void Write(string path, params string[] lines)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    public static void Append(string path, string text) => File.AppendAllText(path, text);
}

/// <summary>A session reader over a test folder. Its index starts only when a
/// question needs the folder, so reading single files starts no watcher.</summary>
public static class Readers
{
    public static ClaudeSessionLogReader For(TempDir dir) => new(new TranscriptIndex(dir.Path));
}
