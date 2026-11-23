using System.IO;
using System.Text;
using System.Text.Json;
using StickyAIusage.Services;

namespace StickyAIusage.Health;

/// <summary>The few file reads the health checks need; tests use a fake.</summary>
public interface IHealthFiles
{
    bool DirectoryExists(string path);

    /// <summary>The whole file when it is at most <paramref name="maxBytes"/>; null when missing, larger or unreadable.</summary>
    string? ReadSmall(string path, int maxBytes);

    /// <summary>The file's last <paramref name="bytes"/> bytes as text, from the first line that
    /// starts in them (a line the cut fell into is left out); null when missing or unreadable.</summary>
    string? ReadTail(string path, int bytes);
}

public sealed class DiskHealthFiles : IHealthFiles
{
    public static readonly DiskHealthFiles Instance = new();

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public string? ReadSmall(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes)
                return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? ReadTail(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - bytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            if (start == 0)
                return text;
            var firstLineEnd = text.IndexOf('\n');
            return firstLineEnd < 0 ? "" : text[(firstLineEnd + 1)..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads what the health checks need from disk: Claude Code's folder, the
/// version in the newest transcript (from the transcript index: the folder is
/// never walked again), the plugin's marker, and whether Claude Code lists the
/// plugin as installed. Starts no process and reads nothing else.
/// </summary>
public static class HealthProbe
{
    /// <summary>How much of the newest transcript is read for its version.</summary>
    public const int TranscriptTailBytes = 256 * 1024;

    private const int SmallFileBytes = 256 * 1024;

    /// <param name="newestTranscript">The index's newest session transcript; null: none.</param>
    public static (bool Exists, string? ClaudeCodeVersion, PluginMarker? Marker, bool PluginInstalled) Read(
        IHealthFiles files, string claudeDirectory, string feedDirectory, string? newestTranscript)
    {
        var exists = files.DirectoryExists(claudeDirectory);
        var version = newestTranscript is null ? null : VersionIn(files.ReadTail(newestTranscript, TranscriptTailBytes));
        var marker = ParseMarker(files.ReadSmall(Path.Combine(feedDirectory, ClaudeFeedUsageSource.MarkerFileName), SmallFileBytes));
        var installed = exists && ListsPlugin(files.ReadSmall(Path.Combine(claudeDirectory, "plugins", "installed_plugins.json"), SmallFileBytes));
        return (exists, version, marker, installed);
    }

    /// <summary>The <c>version</c> field of the last transcript line that has
    /// one (Claude Code writes it on every message).</summary>
    public static string? VersionIn(string? tail)
    {
        if (string.IsNullOrEmpty(tail))
            return null;
        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('{') || !line.Contains("\"version\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("version", out var value) &&
                    value.ValueKind == JsonValueKind.String && HealthCheck.ParseVersion(value.GetString()) is not null)
                    return value.GetString();
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    public static PluginMarker? ParseMarker(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            DateTimeOffset? at = root.TryGetProperty("observedAt", out var observed) && observed.ValueKind == JsonValueKind.Number &&
                                 observed.TryGetInt64(out var ms) && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            return new PluginMarker(Text(root, "pluginVersion"), Text(root, "claudeCodeVersion"), Text(root, "surface"), at);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }

        static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Claude Code's list of installed plugins names ours (from any marketplace).</summary>
    public static bool ListsPlugin(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("plugins", out var plugins) &&
                   plugins.ValueKind == JsonValueKind.Object &&
                   plugins.EnumerateObject().Any(p => p.Name.StartsWith(PluginInstaller.PluginName + "@", StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
