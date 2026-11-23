using StickyAIusage.Health;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>What Health reads from disk: the newest transcript's version (from
/// its tail, through the index), the plugin's marker, Claude Code's list of
/// installed plugins - and nothing else.</summary>
public class HealthProbeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_version_is_the_last_whole_line_that_has_one()
    {
        var tail = """
            ,"cut":"the end of a line cut by the tail","version":"9.9.9"}
            {"type":"user","version":"2.1.318","message":{"content":"\"version\":\"0.0.1\""}}
            {"type":"assistant","version":"2.1.325"}
            {"type":"summary"}
            """;
        Assert.Equal("2.1.325", HealthProbe.VersionIn(tail));
    }

    [Fact]
    public void A_version_inside_a_message_is_not_the_files_version()
    {
        var tail = "x\n" + """{"type":"user","message":{"content":"{\"version\":\"1.2.3\"}"}}""";
        Assert.Null(HealthProbe.VersionIn(tail));
    }

    [Fact]
    public void A_whole_file_of_one_line_is_read_and_junk_is_skipped()
    {
        Assert.Equal("2.1.300", HealthProbe.VersionIn("""{"version":"2.1.300"}"""));
        Assert.Null(HealthProbe.VersionIn("""{"version":"not a version"}"""));
        Assert.Null(HealthProbe.VersionIn("garbage\n{broken"));
        Assert.Null(HealthProbe.VersionIn(""));
        Assert.Null(HealthProbe.VersionIn(null));
    }

    [Fact]
    public void The_marker_says_which_plugin_and_when()
    {
        var marker = HealthProbe.ParseMarker("""{"schema":1,"pluginVersion":"2.3.0","claudeCodeVersion":"2.1.325","surface":"terminal","observedAt":1794000000000}""")!;
        Assert.Equal(("2.3.0", "2.1.325", "terminal"), (marker.PluginVersion, marker.ClaudeCodeVersion, marker.Surface));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794000000000), marker.ObservedAt);

        Assert.Null(HealthProbe.ParseMarker("not json"));
        Assert.Null(HealthProbe.ParseMarker("[1]"));
        Assert.Null(HealthProbe.ParseMarker(null));
        Assert.Null(HealthProbe.ParseMarker("""{"observedAt":"soon"}""")!.ObservedAt);
    }

    [Theory]
    [InlineData("""{"version":2,"plugins":{"sticky-usage@sticky-ai-usage":[{"scope":"user"}]}}""", true)]
    [InlineData("""{"version":2,"plugins":{"sticky-usage@inline":[]}}""", true)]
    [InlineData("""{"version":2,"plugins":{"sticky-usage-other@x":[],"other@sticky-ai-usage":[]}}""", false)]
    [InlineData("""{"version":2,"plugins":{}}""", false)]
    [InlineData("""{"plugins":[]}""", false)]
    [InlineData("broken", false)]
    [InlineData(null, false)]
    public void Claude_codes_plugin_list_is_read_for_our_name_only(string? json, bool listed) =>
        Assert.Equal(listed, HealthProbe.ListsPlugin(json));

    [Fact]
    public void Read_takes_only_the_three_files_it_names()
    {
        var claude = _dir.Combine("claude");
        var feed = _dir.Combine("feed");
        var transcript = Path.Combine(claude, "projects", "p", Guid.NewGuid() + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        Directory.CreateDirectory(Path.Combine(claude, "plugins"));
        Directory.CreateDirectory(feed);
        File.WriteAllText(transcript, new string('x', HealthProbe.TranscriptTailBytes) + "\n" + """{"version":"2.1.325"}""" + "\n");
        File.WriteAllText(Path.Combine(feed, "plugin.json"), """{"pluginVersion":"2.3.0","observedAt":1794000000000}""");
        File.WriteAllText(Path.Combine(claude, "plugins", "installed_plugins.json"), """{"plugins":{"sticky-usage@sticky-ai-usage":[]}}""");
        File.WriteAllText(Path.Combine(claude, ".credentials.json"), "secret");
        var files = new CountingFiles();

        var (exists, version, marker, installed) = HealthProbe.Read(files, claude, feed, transcript);

        Assert.Equal((true, "2.1.325", "2.3.0", true), (exists, version, marker?.PluginVersion, installed));
        Assert.Equal(
            [transcript, Path.Combine(feed, "plugin.json"), Path.Combine(claude, "plugins", "installed_plugins.json")],
            files.Read);
    }

    [Fact]
    public void Missing_files_are_unknown_facts_not_errors()
    {
        var (exists, version, marker, installed) = HealthProbe.Read(DiskHealthFiles.Instance, _dir.Combine("none"), _dir.Combine("feed"), _dir.Combine("x.jsonl"));
        Assert.Equal((false, null, null, false), (exists, version, marker, installed));
        Assert.Null(DiskHealthFiles.Instance.ReadSmall(_dir.Combine("none.json"), 10));
        File.WriteAllText(_dir.Combine("big.json"), new string(' ', 20));
        Assert.Null(DiskHealthFiles.Instance.ReadSmall(_dir.Combine("big.json"), 10));
        // A tail starts at a line; the whole file is all of it.
        var tailFile = WriteAndReturn(_dir.Combine("t.txt"), "xx\nab");
        Assert.Equal("ab", DiskHealthFiles.Instance.ReadTail(tailFile, 4));
        Assert.Equal("", DiskHealthFiles.Instance.ReadTail(tailFile, 2));
        Assert.Equal("xx\nab", DiskHealthFiles.Instance.ReadTail(tailFile, 100));
    }

    private static string WriteAndReturn(string path, string text)
    {
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task The_service_asks_the_index_and_never_walks_the_transcript_folder_again()
    {
        var claude = _dir.Combine("claude");
        var transcript = Path.Combine(claude, "projects", "p", Guid.NewGuid() + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        var now = DateTimeOffset.Now;
        Transcript.Write(transcript, Transcript.User("s1", now.AddMinutes(-2)));
        using var sessions = new ClaudeSessionLogSource(Path.Combine(claude, "projects"));
        var index = sessions.Index;
        using var services = AppServices.Create(new AppSettings(), sessions, _dir.Combine("feed"), watchFeed: false,
            _ => throw new InvalidOperationException("no endpoint"), cacheDirectory: _dir.Combine("cache"), ownsSessions: false);
        index.WaitUntilReady(TimeSpan.FromSeconds(10));
        var walks = index.WalkCount;

        var result = await HealthService.CheckAsync(services, new AppSettings(), null, null, "test build");

        Assert.Equal(walks, index.WalkCount);
        Assert.Equal(10, result.Items.Count);
        Assert.Contains("test build", result.Report);
        Assert.Equal(HealthStatus.Fail, result.Items.Single(i => i.Id == "feed").Status);

        // Check for updates switched on in Settings, the services not rebuilt yet (no checker):
        // Health names the build's own version instead of calling it versionless.
        var switchedOn = await HealthService.CheckAsync(services, new AppSettings { CheckForUpdates = true }, null, null, "test build");
        Assert.StartsWith("On: once a day the app asks GitHub", switchedOn.Items.Single(i => i.Id == "update").Detail);
    }

    private sealed class CountingFiles : IHealthFiles
    {
        public List<string> Read { get; } = [];
        public bool DirectoryExists(string path) => DiskHealthFiles.Instance.DirectoryExists(path);

        public string? ReadSmall(string path, int maxBytes)
        {
            Read.Add(path);
            return DiskHealthFiles.Instance.ReadSmall(path, maxBytes);
        }

        public string? ReadTail(string path, int bytes)
        {
            Read.Add(path);
            return DiskHealthFiles.Instance.ReadTail(path, bytes);
        }
    }
}
