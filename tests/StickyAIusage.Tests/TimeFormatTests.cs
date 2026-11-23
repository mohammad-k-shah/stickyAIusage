using System.Globalization;
using StickyAIusage.Models;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Times of day follow the computer's clock format: "3:05 PM" on a
/// 12-hour clock, "15:05" on a 24-hour one. The test projects pin en-US, so
/// each case sets its culture.</summary>
public class TimeFormatTests
{
    private static readonly DateTime LocalStart = new(2026, 11, 17, 15, 5, 0);

    private static SessionInfo Session() => new()
    {
        SessionId = "s1",
        TranscriptPath = "s1.jsonl",
        WorkingDirectory = "app",
        StartedAt = new DateTimeOffset(LocalStart, TimeZoneInfo.Local.GetUtcOffset(LocalStart)),
        LastActivityAt = new DateTimeOffset(LocalStart, TimeZoneInfo.Local.GetUtcOffset(LocalStart)).AddMinutes(20)
    };

    private static string? InCulture(string name, Func<string?> format)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            return format();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("en-US", "3:05 PM")]
    [InlineData("de-DE", "15:05")]
    [InlineData("en-GB", "15:05")]
    public void A_card_s_start_time_follows_the_clock_format(string culture, string expected) =>
        Assert.Equal(expected, InCulture(culture, () => SessionFields.Find("started")!.Value(Session())));

    [Theory]
    [InlineData("en-US", "Resets Fri 3:30 PM")]
    [InlineData("de-DE", "Resets Fri 15:30")]
    public void Reset_times_follow_the_clock_format(string culture, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);   // Tuesday
        var reset = new DateTimeOffset(2026, 11, 20, 15, 30, 0, TimeSpan.Zero); // Friday
        Assert.Equal(expected, Format.ResetsAt(reset, now, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo(culture)));
    }

    [Theory]
    [InlineData("en-US", "4:12 PM", "4:12")]
    [InlineData("de-DE", "16:12", "16:12")]
    public void Limit_times_follow_the_clock_format(string culture, string full, string compact)
    {
        var at = new DateTimeOffset(2026, 11, 17, 16, 12, 0, TimeSpan.Zero);
        var info = CultureInfo.GetCultureInfo(culture);
        Assert.Equal(full, Format.ShortTime(at, TimeZoneInfo.Utc, info));
        Assert.Equal(compact, Format.ShortTimeNoDesignator(at, TimeZoneInfo.Utc, info));
    }

    [Fact]
    public void A_narrow_no_break_space_before_PM_becomes_a_plain_space()
    {
        // en-US as newer ICU data has it (macOS, Linux): "h:mm tt". This PC's
        // Windows data uses a plain space, so the clone makes the case on any OS.
        var icu = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        icu.DateTimeFormat.ShortTimePattern = "h:mm tt";
        var at = new DateTimeOffset(2026, 11, 17, 16, 12, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal("4:12 PM", Format.ShortTime(at, TimeZoneInfo.Utc, icu));
        Assert.Equal("Resets today 4:12 PM", Format.ResetsAt(at, now, TimeZoneInfo.Utc, icu));
        Assert.Equal("4:12", Format.ShortTimeNoDesignator(at, TimeZoneInfo.Utc, icu));
    }
}
