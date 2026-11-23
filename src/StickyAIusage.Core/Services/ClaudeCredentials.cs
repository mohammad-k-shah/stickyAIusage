using System.Text.Json;

namespace StickyAIusage.Services;

/// <summary>
/// What the Legacy and Automatic usage sources read from Claude Code's stored sign-in
/// (<c>{"claudeAiOauth": {accessToken, expiresAt, refreshTokenExpiresAt, subscriptionType, ...}}</c>
/// in the credentials file on Windows, the "Claude Code-credentials" Keychain item on macOS):
/// read once per fetch, never changed. The app never renews the token and never writes the
/// credentials: Claude Code renews its own sign-in whenever it runs. When the token has
/// expired, the source waits for Claude Code to do that instead of asking with a dead token.
/// </summary>
/// <param name="ExpiresAt">When the access token expires; null when the credentials don't say.</param>
/// <param name="RefreshTokenExpiresAt">When the sign-in itself ends (only a new sign-in helps then); null: not said.</param>
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt, DateTimeOffset? RefreshTokenExpiresAt,
    string? SubscriptionType)
{
    /// <summary>A token this close to its expiry is treated as expired: the request would
    /// likely arrive after it, and an answer of 401 teaches nothing new.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);

    /// <summary>The access token has expired (or is about to): Claude Code renews it when it next runs.</summary>
    public bool AccessTokenExpired(DateTimeOffset now) => ExpiresAt is { } at && at - ExpiryMargin <= now;

    /// <summary>The sign-in itself has ended: only signing in again helps.</summary>
    public bool SignInExpired(DateTimeOffset now) => RefreshTokenExpiresAt is { } at && at <= now;

    /// <summary>One read of the store (on macOS one <c>security</c> process); null when there
    /// is no sign-in or it can't be read. Never throws.</summary>
    public static ClaudeCredentials? Read(ICredentialStore store)
    {
        try
        {
            return store.Read() is { } json ? Parse(json) : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ClaudeCredentials.Read", ex);
            return null;
        }
    }

    /// <summary>The fields above from the credentials JSON; null without an access token or
    /// when the text isn't the expected JSON. Unknown fields are ignored.</summary>
    internal static ClaudeCredentials? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String ||
                token.GetString() is not { Length: > 0 } accessToken)
                return null;
            return new ClaudeCredentials(accessToken, Time(oauth, "expiresAt"), Time(oauth, "refreshTokenExpiresAt"),
                oauth.TryGetProperty("subscriptionType", out var plan) && plan.ValueKind == JsonValueKind.String ? plan.GetString() : null);
        }
        catch (JsonException ex)
        {
            DiagnosticLog.WriteError("ClaudeCredentials.Parse", ex);
            return null;
        }
    }

    private static DateTimeOffset? Time(JsonElement oauth, string name) =>
        oauth.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ms) &&
        ms is > 0 and < 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    /// <summary>The token never appears in logs or exceptions, even through a record's ToString.</summary>
    public override string ToString() =>
        $"ClaudeCredentials {{ ExpiresAt = {ExpiresAt:O}, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O}, SubscriptionType = {SubscriptionType} }}";
}
