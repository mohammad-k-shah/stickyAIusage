using System.Text.RegularExpressions;
using StickyAIusage.Tests.Shared;
using Xunit;

namespace StickyAIusage.Tests;

/// <summary>
/// The READMEs show only pictures the screenshot tests draw from the demo (one script refreshes them
/// all, <c>scripts/refresh-screenshots.ps1</c>), every drawn picture is used, nothing else sits in
/// <c>docs/images</c>, and every picture has a width (they are drawn at twice their size).
/// </summary>
public partial class ReadmePictureTests
{
    private static readonly string[] ReadmeFiles = ["README.md", "README.zh-CN.md", "README.ja.md", "README.ko.md"];

    private static readonly HashSet<string> Known =
        ReadmePictures.Rendered.Concat(ReadmePictures.HandMade).ToHashSet(StringComparer.Ordinal);

    public static TheoryData<string> Readmes => new(ReadmeFiles);

    [Theory]
    [MemberData(nameof(Readmes))]
    public void Every_picture_a_readme_shows_is_drawn_by_the_tests(string readme)
    {
        var used = PicturesIn(File.ReadAllText(Path.Combine(RepositoryRoot(), readme))).ToList();
        Assert.NotEmpty(used);
        Assert.All(used, picture => Assert.Contains(picture, Known));
    }

    [Theory]
    [MemberData(nameof(Readmes))]
    public void Every_picture_has_a_width(string readme)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), readme));
        var tags = ImageTag().Matches(text).Select(m => m.Value).ToList();
        Assert.NotEmpty(tags);
        Assert.All(tags, tag => Assert.Matches("""\swidth="\d+"\s*""", tag));
        // Markdown pictures can't carry a width.
        Assert.DoesNotMatch(@"!\[[^\]]*\]\(docs/images/", text);
    }

    [Fact]
    public void Every_drawn_picture_is_used_and_nothing_else_is_in_the_images_folder()
    {
        var root = RepositoryRoot();
        var english = PicturesIn(File.ReadAllText(Path.Combine(root, "README.md"))).ToHashSet(StringComparer.Ordinal);
        // The social preview is uploaded in the repository's settings, not shown in a README.
        foreach (var picture in ReadmePictures.Rendered.Concat(ReadmePictures.HandMade).Where(p => p != "social-preview.png"))
            Assert.Contains(picture, english);

        var images = Path.Combine(root, "docs", "images");
        var present = Directory.EnumerateFiles(images, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(images, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Known.Order(StringComparer.Ordinal), present);
    }

    [Fact]
    public void The_demo_gif_and_social_preview_have_the_sizes_their_places_ask_for()
    {
        var images = Path.Combine(RepositoryRoot(), "docs", "images");
        var preview = File.ReadAllBytes(Path.Combine(images, "social-preview.png"));
        Assert.Equal((1280, 640), (BigEndian(preview, 16), BigEndian(preview, 20)));
        Assert.True(preview.Length < 1024 * 1024, "GitHub takes a social preview under 1 MB");

        var gif = File.ReadAllBytes(Path.Combine(images, "demo.gif"));
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(gif, 0, 6));
        Assert.Equal(800, gif[6] | gif[7] << 8);
        Assert.True(gif.Length < 5 * 1024 * 1024, "the demo GIF stays under 5 MB");
    }

    private static int BigEndian(byte[] bytes, int at) => bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3];

    private static IEnumerable<string> PicturesIn(string text) =>
        Picture().Matches(text).Select(m => m.Groups[1].Value);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "StickyAIusage.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("The repository root (StickyAIusage.sln) is not above the test folder.");
    }

    [GeneratedRegex(@"docs/images/([A-Za-z0-9_./-]+\.(?:png|gif))")]
    private static partial Regex Picture();

    [GeneratedRegex(@"<img\s[^>]*>")]
    private static partial Regex ImageTag();
}
