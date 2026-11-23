using System.Text;

namespace StickyAIusage.Alerts;

/// <summary>
/// Turns the alerts of one refresh into one notification: Windows shows one
/// balloon at a time (the rest queue behind it), and two windows crossing a
/// threshold together are one piece of news. The most urgent alert gives the
/// title and text; the others are named after "Also:". A copy that isn't the
/// default profile says so in the title. Lengths stay inside what Windows
/// takes (title 63, text 255 characters).
/// </summary>
public static class AlertMessages
{
    public const int MaxTitle = 63;
    public const int MaxBody = 255;

    public static (string Title, string Body) ForNotification(IReadOnlyList<Alert> alerts, string? instanceLabel = null)
    {
        if (alerts.Count == 0)
            throw new ArgumentException("No alerts to show.", nameof(alerts));
        var ordered = alerts.OrderByDescending(a => a.Level).ToList();
        var lead = ordered[0];
        var title = instanceLabel is null ? lead.Title : $"{lead.Title} ({instanceLabel})";
        var body = lead.Body;
        if (ordered.Count > 1)
            body += " Also: " + string.Join("; ", ordered.Skip(1).Select(a => a.Title)) + ".";
        return (Cut(title, MaxTitle), Cut(body, MaxBody));
    }

    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var end = max - 1;
        if (char.IsHighSurrogate(text[end - 1]))
            end--; // never split an emoji in half
        return text[..end].TrimEnd() + "…";
    }
}

/// <summary>
/// The macOS banner: <c>osascript -e 'display notification "…" with title "…"'</c>
/// (Standard Additions). The texts go inside AppleScript string literals, so
/// backslashes and double quotes are escaped and line breaks become spaces;
/// any other character, accents and emoji included, passes as it is (osascript
/// reads its arguments as UTF-8). The banner is attributed to the script
/// runner (Script Editor in System Settings → Notifications).
/// </summary>
public static class AppleScriptNotification
{
    /// <summary>The arguments for <c>/usr/bin/osascript</c>, one per entry.</summary>
    public static IReadOnlyList<string> Arguments(string title, string body) =>
        ["-e", $"display notification {Literal(body)} with title {Literal(title)}"];

    /// <summary>An AppleScript string literal: "say \"hi\"".</summary>
    public static string Literal(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r' or '\n' or '\t' or (char)0x2028 or (char)0x2029: builder.Append(' '); break;
                default:
                    if (!char.IsControl(c))
                        builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }
}
