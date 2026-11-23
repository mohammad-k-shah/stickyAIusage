using System.Globalization;

namespace StickyAIusage.Alerts;

/// <summary>
/// A daily stretch of local time without alerts, such as 22:00 to 07:00. It
/// may cross midnight; <see cref="From"/> is inside it, <see cref="To"/> is not.
/// Stored in settings.json as two "HH:mm" texts (<c>AlertQuietFrom</c>,
/// <c>AlertQuietTo</c>); both null means none.
/// </summary>
public sealed record QuietHours(TimeOnly From, TimeOnly To)
{
    private static readonly string[] Formats =
        ["H:mm", "HH:mm", "%H", "HH","h:mm tt", "h:mmtt", "h tt", "htt", "hh:mm tt", "H.mm"];

    /// <summary>True when <paramref name="now"/>, in <paramref name="zone"/>, falls inside.</summary>
    public bool Contains(DateTimeOffset now, TimeZoneInfo zone)
    {
        var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return From < To
            ? time >= From && time < To
            : time >= From || time < To; // crosses midnight
    }

    /// <summary>"22:00", the form settings.json keeps.</summary>
    public static string ToSetting(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The quiet hours a settings file names; null when off or unreadable.</summary>
    public static QuietHours? FromSettings(string? from, string? to) =>
        TryParseTime(from, out var start) && TryParseTime(to, out var end) && start != end ? new QuietHours(start, end) : null;

    /// <summary>A time someone typed: "22:00", "7", "10 PM", "10:30pm", "22.30".</summary>
    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var cleaned = text.Trim().ToUpperInvariant().Replace("A.M.", "AM").Replace("P.M.", "PM");
        return TimeOnly.TryParseExact(cleaned, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time);
    }

    /// <summary>What Settings checks before Save. Off: always valid.</summary>
    public sealed record Result(bool IsValid, TimeOnly From, TimeOnly To, bool FromInvalid, bool ToInvalid, string? Error);

    public static Result Validate(bool on, string from, string to)
    {
        if (!on)
            return new Result(true, default, default, false, false, null);
        var fromOk = TryParseTime(from, out var start);
        var toOk = TryParseTime(to, out var end);
        if (!fromOk || !toOk)
            return new Result(false, start, end, !fromOk, !toOk, "Enter times like 22:00 or 7:30.");
        if (start == end)
            return new Result(false, start, end, true, true, "Quiet hours need a different start and end.");
        return new Result(true, start, end, false, false, null);
    }
}
