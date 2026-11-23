using StickyAIusage.Sharing;

namespace StickyAIusage.Tests;

public class ShareFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private sealed class FakeOutput : IShareOutput
    {
        public bool CopyWorks = true;
        public bool OpenWorks = true;
        public string? SavePath = @"/Users/me/Pictures/mine.png";
        public string? PicturesPath = @"/Users/me/Pictures/usage-20261118-1400.png";
        public bool Throws;
        public readonly List<string> Calls = [];

        public Task<bool> CopyImageAsync(byte[] png)
        {
            Calls.Add("copy");
            return Throws ? throw new InvalidOperationException("boom") : Task.FromResult(CopyWorks);
        }

        public Task<string?> SaveImageAsync(byte[] png, string suggestedName)
        {
            Calls.Add("save " + suggestedName);
            return Task.FromResult(SavePath);
        }

        public Task<string?> SaveToPicturesAsync(byte[] png, string fileName)
        {
            Calls.Add("pictures " + fileName);
            return Task.FromResult(PicturesPath);
        }

        public Task<bool> OpenUrlAsync(string url)
        {
            Calls.Add("open " + url);
            return Task.FromResult(OpenWorks);
        }
    }

    private static Task<string?> Run(ShareAction action, FakeOutput output) =>
        ShareFlow.RunAsync(action, Png, ShareCardVariant.Limits, output, Now, TimeZoneInfo.Utc);

    [Fact]
    public async Task Copy_puts_the_image_on_the_clipboard()
    {
        var output = new FakeOutput();
        Assert.Equal(ShareFlow.Copied, await Run(ShareAction.Copy, output));
        Assert.Equal(["copy"], output.Calls);
    }

    [Fact]
    public async Task A_refused_clipboard_saves_the_card_to_Pictures_and_says_where()
    {
        var output = new FakeOutput { CopyWorks = false };
        Assert.Equal(@"Couldn't copy the image, so it was saved instead: /Users/me/Pictures/usage-20261118-1400.png",
            await Run(ShareAction.Copy, output));
        Assert.Equal(["copy", "pictures usage-20261118-1400.png"], output.Calls);

        output = new FakeOutput { CopyWorks = false, PicturesPath = null };
        Assert.Equal(ShareFlow.NothingWorked, await Run(ShareAction.Copy, output));
    }

    [Fact]
    public async Task Save_suggests_the_dated_name_and_a_cancel_says_nothing()
    {
        var output = new FakeOutput();
        Assert.Equal("Saved mine.png.", await Run(ShareAction.Save, output));
        Assert.Equal(["save usage-20261118-1400.png"], output.Calls);

        Assert.Null(await Run(ShareAction.Save, new FakeOutput { SavePath = null }));
    }

    [Fact]
    public async Task Post_copies_the_image_then_opens_the_site()
    {
        var output = new FakeOutput();
        Assert.Equal("Image copied. Paste it into the X post that just opened.", await Run(ShareAction.PostX, output));
        Assert.Equal(["copy", "open " + ShareCards.PostUrl(ShareTarget.X, ShareCardVariant.Limits)], output.Calls);

        output = new FakeOutput();
        await Run(ShareAction.PostBluesky, output);
        Assert.StartsWith("open https://bsky.app/intent/compose?text=", output.Calls[1]);
        output = new FakeOutput();
        await Run(ShareAction.PostLinkedIn, output);
        Assert.StartsWith("open https://www.linkedin.com/sharing/share-offsite/", output.Calls[1]);

        Assert.Equal("Image copied, but LinkedIn didn't open in your browser.",
            await Run(ShareAction.PostLinkedIn, new FakeOutput { OpenWorks = false }));
    }

    [Fact]
    public async Task Post_without_a_clipboard_still_opens_the_site_with_the_saved_card()
    {
        var output = new FakeOutput { CopyWorks = false };
        Assert.Equal(@"Couldn't copy the image, so it was saved to /Users/me/Pictures/usage-20261118-1400.png. Attach it to the Bluesky post that just opened.",
            await Run(ShareAction.PostBluesky, output));
        Assert.Equal(3, output.Calls.Count);
        Assert.Equal(@"Couldn't copy the image or open X; it was saved to /Users/me/Pictures/usage-20261118-1400.png.",
            await Run(ShareAction.PostX, new FakeOutput { CopyWorks = false, OpenWorks = false }));
    }

    [Fact]
    public async Task A_failing_app_side_never_throws()
    {
        Assert.Equal(ShareFlow.NothingWorked, await Run(ShareAction.Copy, new FakeOutput { Throws = true }));
    }

    [Fact]
    public void The_menu_lists_every_action_in_order()
    {
        Assert.Equal(["Copy image", "Save image…", "Post on X", "Post on LinkedIn", "Post on Bluesky"],
            ShareFlow.Actions.Select(ShareFlow.MenuText));
    }

    [Fact]
    public void The_Mac_clipboard_script_reads_the_file_as_PNG_and_escapes_its_path()
    {
        Assert.Equal(["-e", "set the clipboard to (read (POSIX file \"/tmp/a \\\"b\\\".png\") as «class PNGf»)"],
            ShareFlow.MacClipboardArguments("/tmp/a \"b\".png"));
    }
}
