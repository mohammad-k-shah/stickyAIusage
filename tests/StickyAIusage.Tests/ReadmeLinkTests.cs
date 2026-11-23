using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StickyAIusage.Tests;

/// <summary>
/// The README and its translations, and for links every public document (<see cref="PublicDocs"/>): every relative
/// link and image points at a file that exists, every anchor (in the same file or another Markdown file) is a
/// heading there, every translation links the others, and none of the READMEs carries words from the project's
/// private planning (<see cref="PublicDocsTests"/> checks the other documents).
/// </summary>
public partial class ReadmeLinkTests
{
    private static readonly string[] ReadmeFiles = ["README.md", "README.zh-CN.md", "README.ja.md", "README.ko.md"];

    public static TheoryData<string> Readmes => new(ReadmeFiles);

    public static TheoryData<string> Documents => new(PublicDocs.Files());

    /// <summary>Every public document (the READMEs, docs/*.md, the plugin's and packaging's READMEs): a link is
    /// resolved from the document's own folder, as GitHub does.</summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_relative_link_image_and_anchor_resolves(string document)
    {
        var root = RepositoryRoot();
        var own = Path.Combine(root, document);
        var folder = Path.GetDirectoryName(own)!;
        var problems = new List<string>();
        foreach (var target in Targets(File.ReadAllText(own)))
        {
            if (target.StartsWith("http://", StringComparison.Ordinal) || target.StartsWith("https://", StringComparison.Ordinal) ||
                target.StartsWith("mailto:", StringComparison.Ordinal))
                continue;
            var hash = target.IndexOf('#');
            var file = hash < 0 ? target : target[..hash];
            var anchor = hash < 0 ? null : Uri.UnescapeDataString(target[(hash + 1)..]);
            var path = file.Length == 0 ? own : Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(file)));
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                problems.Add($"{target}: no such file");
                continue;
            }
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{target}: outside the repository");
            if (anchor is not null && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                !Anchors(File.ReadAllText(path)).Contains(anchor))
                problems.Add($"{target}: no heading with that anchor");
        }
        Assert.True(problems.Count == 0, document + ":\n" + string.Join('\n', problems));
    }

    [Theory]
    [MemberData(nameof(Readmes))]
    public void Each_readme_links_every_other_language(string readme)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), readme));
        foreach (var other in ReadmeFiles.Where(r => r != readme))
            Assert.Contains($"]({other})", text);
    }

    [Theory]
    [MemberData(nameof(Readmes))]
    public void No_private_planning_words_reach_a_readme(string readme)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), readme));
        // The product has a "day target" (a setting): other uses of "target" would be planning words.
        foreach (var word in PrivateWords.Load())
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        var withoutTheSetting = Regex.Replace(text, @"(day|daily) target|target you set", "", RegexOptions.IgnoreCase);
        Assert.DoesNotContain("target", withoutTheSetting, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Anchors_are_made_the_way_github_makes_them()
    {
        var anchors = Anchors("## 🔑 How it gets its numbers\n### ⏱️ Will you make it to the reset?\n## ⚙️ Settings\n## FAQ\n## FAQ\n```\n## not a heading\n```\n## 简体中文 说明");
        Assert.Equal(["-how-it-gets-its-numbers", "️-will-you-make-it-to-the-reset", "️-settings", "faq", "faq-1", "简体中文-说明"], anchors.ToList());
    }

    /// <summary>Link and image targets: Markdown <c>](…)</c> and HTML <c>src</c>/<c>href</c>.</summary>
    internal static IEnumerable<string> Targets(string markdown)
    {
        foreach (Match m in MarkdownLink().Matches(markdown))
            yield return m.Groups[1].Value;
        foreach (Match m in HtmlLink().Matches(markdown))
            yield return m.Groups[1].Value;
    }

    /// <summary>The anchors GitHub gives a file's headings (github-slugger: lowercase; keep letters, marks, numbers,
    /// connector punctuation, spaces and hyphens; spaces become hyphens; repeats get -1, -2). Code blocks have none.</summary>
    internal static IReadOnlyList<string> Anchors(string markdown)
    {
        var anchors = new List<string>();
        var seen = new Dictionary<string, int>();
        var inCode = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
                continue;
            var heading = Heading().Match(raw);
            if (!heading.Success)
                continue;
            var slug = new StringBuilder();
            foreach (var rune in heading.Groups[1].Value.Trim().ToLowerInvariant().EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (rune.Value == ' ')
                    slug.Append('-');
                else if (rune.Value == '-' || category is UnicodeCategory.ConnectorPunctuation ||
                         Rune.IsLetter(rune) || Rune.IsDigit(rune) || Rune.IsNumber(rune) ||
                         category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                    slug.Append(rune.ToString());
            }
            var anchor = slug.ToString();
            if (seen.TryGetValue(anchor, out var count))
            {
                seen[anchor] = count + 1;
                anchor = $"{anchor}-{count + 1}";
            }
            else
            {
                seen[anchor] = 0;
            }
            anchors.Add(anchor);
        }
        return anchors;
    }

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "StickyAIusage.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("The repository root (StickyAIusage.sln) is not above the test folder.");
    }

    [GeneratedRegex(@"\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?:src|href)=""([^""]+)""")]
    private static partial Regex HtmlLink();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();
}
