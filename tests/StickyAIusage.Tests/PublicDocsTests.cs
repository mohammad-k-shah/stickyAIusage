using System.Text.RegularExpressions;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>
/// The maintainers' word list: words and phrases no public file may contain, and the paths that are not part of the
/// public repository. It is never published, so a public checkout has none and the tests below check only their
/// generic patterns. It is the <c>*guard-words.txt</c> file under <c>docs/</c>: one entry per line, <c>#</c> starts a
/// comment, and <c>skip: &lt;path&gt;</c> names a file, or a folder ending in <c>/</c>, that the scans leave out.
/// </summary>
public static class PrivateWords
{
    private const string SkipPrefix = "skip:";

    /// <summary>The list's full path; null in a checkout without one.</summary>
    public static string? ListPath
    {
        get
        {
            var docs = Path.Combine(ReadmeLinkTests.RepositoryRoot(), "docs");
            return Directory.Exists(docs)
                ? Directory.EnumerateFiles(docs, "*guard-words.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault()
                : null;
        }
    }

    /// <summary>The words and phrases (case-insensitive).</summary>
    public static IReadOnlyList<string> Load() =>
        Entries().Where(entry => !entry.StartsWith(SkipPrefix, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The repository-relative paths the scans leave out, with forward slashes.</summary>
    public static IReadOnlyList<string> Skipped() =>
        Entries().Where(entry => entry.StartsWith(SkipPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry[SkipPrefix.Length..].Trim().Replace('\\', '/'))
            .Where(path => path.Length > 0)
            .ToList();

    private static IEnumerable<string> Entries() =>
        ListPath is { } path
            ? File.ReadLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))
            : [];
}

/// <summary>The files people see in the public repository: every Markdown file (the READMEs, <c>docs/*.md</c> and the
/// other folders' READMEs) and every other text file of the source. What the maintainers' list skips, the list
/// itself, dot folders and build output are never among them.</summary>
public static class PublicDocs
{
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "artifacts", "TestResults"];

    /// <summary>Folders of tools on a developer's machine (not the repository's own dot folders, such as .github).</summary>
    private static readonly string[] ToolFolders = [".git", ".vs", ".vscode", ".idea", ".claude"];

    /// <summary>The extensions of the text files the private-word scan reads.</summary>
    private static readonly string[] TextExtensions =
    [
        ".md", ".cs", ".xaml", ".axaml", ".csproj", ".props", ".sln", ".js", ".mjs", ".ts", ".json", ".jsonl", ".yml",
        ".yaml", ".ps1", ".sh", ".iss", ".plist", ".template", ".txt", ".manifest", ".gitattributes", ".gitignore",
    ];

    /// <summary>The public Markdown files: repository-relative paths with forward slashes, sorted.</summary>
    public static IReadOnlyList<string> Files() => Files(ReadmeLinkTests.RepositoryRoot());

    internal static IReadOnlyList<string> Files(string root)
    {
        var skipped = PrivateWords.Skipped();
        return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(path => Relative(root, path))
            .Where(relative => IsPublic(relative, skipped))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every public text file of the source, the repository's own dot folders (.github, the plugin's
    /// manifest) included: what the private-word scan reads.</summary>
    public static IReadOnlyList<string> TextFiles()
    {
        var root = ReadmeLinkTests.RepositoryRoot();
        var skipped = PrivateWords.Skipped();
        var list = PrivateWords.ListPath is { } path ? Relative(root, path) : null;
        return Walk(root)
            .Select(file => Relative(root, file))
            .Where(relative => relative != list && !IsSkipped(relative, skipped) &&
                               TextExtensions.Any(extension => relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    internal static bool IsPublic(string relative, IReadOnlyList<string> skipped)
    {
        if (IsSkipped(relative, skipped))
            return false;
        var folders = relative.Split('/')[..^1];
        // Dot folders (.git, .claude, the plugin's laid-down types) and build output are not documents.
        return !folders.Any(f => f.StartsWith('.') || SkippedFolders.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsSkipped(string relative, IReadOnlyList<string> skipped) =>
        skipped.Any(path => path.EndsWith('/')
            ? relative.StartsWith(path, StringComparison.OrdinalIgnoreCase)
            : relative.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>The files under <paramref name="folder"/>, without build output, tools' folders and the types
    /// Claude Code lays down beside the plugin's manifest.</summary>
    private static IEnumerable<string> Walk(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(sub);
            if (ToolFolders.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                (name == "types" && Path.GetFileName(folder) == ".claude-plugin"))
                continue;
            foreach (var file in Walk(sub))
                yield return file;
        }
    }
}

/// <summary>
/// The public documents: they exist, they carry no private planning, the roadmap has its three horizons, the README's
/// field count is the catalog's, and DATA-SOURCES.md names every web address, environment variable and data file
/// the code uses.
/// </summary>
public partial class PublicDocsTests
{
    private static string Root => ReadmeLinkTests.RepositoryRoot();

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    public static TheoryData<string> Documents => new(PublicDocs.Files());

    [Fact]
    public void The_community_documents_exist_and_private_files_are_not_public()
    {
        var files = PublicDocs.Files();
        foreach (var expected in new[]
                 {
                     "README.md", "README.zh-CN.md", "README.ja.md", "README.ko.md", "docs/ARCHITECTURE.md", "docs/CHANGELOG.md",
                     "docs/CONTRIBUTING.md", "docs/DATA-SOURCES.md", "docs/PROVIDERS.md", "docs/ROADMAP.md", "plugin/README.md",
                     "packaging/README.md",
                 })
            Assert.Contains(expected, files);
        // Whatever the maintainers' list skips stays out.
        foreach (var skipped in PrivateWords.Skipped())
            Assert.DoesNotContain(files, f => skipped.EndsWith('/')
                ? f.StartsWith(skipped, StringComparison.OrdinalIgnoreCase)
                : f.Equals(skipped, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skipped_paths_dot_folders_and_build_output_are_not_public()
    {
        string[] skipped = ["notes/", "PLAN.md"];
        Assert.False(PublicDocs.IsPublic("notes/today.md", skipped));
        Assert.False(PublicDocs.IsPublic("PLAN.md", skipped));
        Assert.True(PublicDocs.IsPublic("docs/PLAN.md", skipped));
        Assert.True(PublicDocs.IsPublic("notes.md", skipped));
        Assert.False(PublicDocs.IsPublic("plugin/.claude-plugin/types/README.md", []));
        Assert.False(PublicDocs.IsPublic("src/StickyAIusage/obj/project.assets.json", []));
        Assert.True(PublicDocs.IsPublic("docs/ROADMAP.md", []));
    }

    [Fact]
    public void No_public_file_of_any_kind_carries_a_private_word()
    {
        var words = PrivateWords.Load();
        if (words.Count == 0)
            return; // a public checkout: no list to check against
        var found = new List<string>();
        foreach (var file in PublicDocs.TextFiles())
        {
            var text = Read(file);
            found.AddRange(words.Where(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)).Select(word => $"{file}: {word}"));
        }
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void No_public_document_carries_private_planning(string document)
    {
        var text = Read(document);
        var found = PrivateWords.Load()
            .Where(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // Planning item ids, and the old backlog's ids next to a number ("P2 (74").
        found.AddRange(ItemId().Matches(text).Select(m => m.Value));
        Assert.True(found.Count == 0, $"{document}: {string.Join(", ", found)}");
    }

    [Fact]
    public void A_word_list_when_there_is_one_has_words_and_paths_that_exist()
    {
        if (PrivateWords.ListPath is not { } list)
            return; // a public checkout: nothing private to guard
        Assert.True(PrivateWords.Load().Count >= 10, $"{list} is nearly empty");
        var skipped = PrivateWords.Skipped();
        Assert.NotEmpty(skipped);
        var root = ReadmeLinkTests.RepositoryRoot();
        // A misspelled path would publish nothing by itself, but it would stop guarding what it meant.
        foreach (var path in skipped)
            Assert.True(File.Exists(Path.Combine(root, path)) || Directory.Exists(Path.Combine(root, path)), $"{list}: no {path}");
    }

    [Fact]
    public void The_roadmap_says_now_next_and_later_without_dates_or_ids()
    {
        var roadmap = Read("docs/ROADMAP.md");
        var headings = ReadmeLinkTests.Anchors(roadmap);
        Assert.Contains(headings, h => h.StartsWith("now", StringComparison.Ordinal));
        Assert.Contains("next", headings);
        Assert.Contains("later", headings);
        Assert.DoesNotMatch(@"\b20\d\d-\d\d-\d\d\b", roadmap);
    }

    [Fact]
    public void The_readme_field_count_is_the_catalog_s()
    {
        var counts = FieldCount().Matches(Read("README.md")).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(counts);
        Assert.All(counts, count => Assert.Equal(SessionFields.All.Count, count));
    }

    [Fact]
    public void Data_sources_names_every_web_address_the_code_uses()
    {
        var hosts = CodeLines()
            .SelectMany(line => Address().Matches(line).Select(m => m.Groups[1].Value))
            .Where(host => host != "www.apple.com") // the LaunchAgent plist's DTD name, not a request
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("api.anthropic.com", hosts); // the scan itself works
        AllNamed(hosts);
    }

    [Fact]
    public void Data_sources_names_every_environment_variable_the_code_or_the_plugin_reads()
    {
        var names = CodeLines().SelectMany(line => EnvironmentName().Matches(line).Select(m => m.Groups[1].Value))
            .Concat(PluginSources().SelectMany(text => PluginEnvironmentName().Matches(text).Select(m => m.Groups[1].Value)))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("STICKYAIUSAGE_DATA_DIR", names);
        Assert.Contains("XDG_DATA_HOME", names);
        AllNamed(names);
    }

    [Fact]
    public void Data_sources_names_every_data_file_the_code_or_the_plugin_reads_or_writes()
    {
        var names = CodeLines().Concat(PluginSources())
            .SelectMany(text => DataFileName().Matches(text).Select(m => m.Groups[1].Value))
            .Where(name => name != "StickyAIusage.Resources.models.json") // the embedded resource's name, not a file
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("journal.jsonl", names);
        Assert.Contains("forecast.json", names);
        AllNamed(names);
    }

    private static void AllNamed(IEnumerable<string> names)
    {
        var page = Read("docs/DATA-SOURCES.md");
        var missing = names.Where(name => !page.Contains(name, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "docs/DATA-SOURCES.md doesn't mention: " + string.Join(", ", missing));
    }

    /// <summary>The apps' and Core's C# lines, without comment lines (they quote examples).</summary>
    private static IEnumerable<string> CodeLines() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(File.ReadLines)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

    /// <summary>The plugin's hooks module, without its tests.</summary>
    private static IEnumerable<string> PluginSources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "plugin", "hooks"), "*.js").Select(File.ReadAllText);

    [GeneratedRegex(@"\b[A-Z]\d-\d\d\b|\b[FPVMDA]\d \(\d\d")]
    private static partial Regex ItemId();

    [GeneratedRegex(@"\b(\d+) fields\b")]
    private static partial Regex FieldCount();

    [GeneratedRegex(@"""https?://([A-Za-z0-9.-]+)")]
    private static partial Regex Address();

    [GeneratedRegex(@"""((?:STICKYAIUSAGE_|CLAUDE_|CODEX_)[A-Z_]+)""")]
    private static partial Regex EnvironmentName();

    [GeneratedRegex(@"env\.get\('([A-Z_]+)'\)")]
    private static partial Regex PluginEnvironmentName();

    [GeneratedRegex(@"[""']((?:\.?[A-Za-z][A-Za-z_-]*)(?:\.[A-Za-z_-]+)*\.(?:json|jsonl|txt|log|lock|signal))[""']")]
    private static partial Regex DataFileName();
}
