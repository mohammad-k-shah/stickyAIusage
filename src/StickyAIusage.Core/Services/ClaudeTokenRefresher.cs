using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StickyAIusage.Services;

/// <summary>
/// Silently refreshes the stored OAuth access token (the .credentials.json
/// file on Windows, the "Claude Code-credentials" Keychain item on macOS -
/// see ICredentialStore) before it expires, the same way Claude Code CLI/Desktop
/// do internally - so the widget's usage-endpoint calls stay authenticated without the user having
/// to manually reopen Claude Desktop or run `claude` periodically. Without this,
/// the stored access token expires roughly hourly even while Claude Desktop is
/// in use.
///
/// This exact refresh mechanism isn't in Anthropic's public docs. The
/// endpoint, client_id, and request/response shape are taken from public,
/// community-documented reverse-engineering of Claude Code's own OAuth flow:
///   https://gist.github.com/ben-vargas/c7c7cbfebbb47278f45feca9cef309d1
///   https://gist.github.com/cedws/3a24b2c7569bb610e24aa90dd217d9f2
/// and cross-checked against the property names Claude Code writes
/// (accessToken/refreshToken/expiresAt/refreshTokenExpiresAt/scopes/
/// subscriptionType/rateLimitTier).
///
/// Sharing the store with Claude Code: every running Claude Code session
/// already reads and refreshes this one entry, so writing refreshed tokens
/// back to it is the cooperative choice - keeping them in a private copy
/// would, if the server rotates refresh tokens, leave Claude Code holding a
/// dead one. Two safeguards make the write-back safe: the store is re-read
/// just before writing, and if another process changed it while our request
/// was in flight, its tokens win and ours are not written; and each store
/// writes atomically (temp file + rename; a single Keychain update).
///
/// This is a best-effort layer on top of ClaudeUsageProvider's 401 handling,
/// not a replacement: if a refresh fails for any reason, this returns the old
/// token unchanged and the normal 401 -> backoff -> "sign in to Claude" path
/// takes over.
/// </summary>
public static class ClaudeTokenRefresher
{
    private const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";

    // Anthropic's public OAuth client id for Claude Code (not a secret - the
    // same value ships inside Claude Code itself and is used with a
    // refresh_token grant, never a password/API key).
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    // Refresh a bit before actual expiry, so a slow request or clock skew
    // doesn't leave us presenting an already-expired token to the API.
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(2);

    /// <summary>Returns a valid access token, refreshing it first if it's
    /// expired or within ExpiryBuffer of expiring. Returns the on-disk token
    /// unchanged if it's still valid, or as a fallback if refreshing fails.
    /// Returns null only if there's no credentials file / no token at all -
    /// matching ClaudeCredentialsReader.TryReadAccessToken's contract.</summary>
    public static Task<string?> GetValidAccessTokenAsync(HttpClient httpClient, CancellationToken ct = default) =>
        GetValidAccessTokenAsync(httpClient, ClaudeCredentialsReader.ForCurrentPlatform(), ct);

    internal static Task<string?> GetValidAccessTokenAsync(HttpClient httpClient, string credentialsPath, CancellationToken ct) =>
        GetValidAccessTokenAsync(httpClient, new FileCredentialStore(credentialsPath), ct);

    /// <summary>True when the stored refresh token's own expiry has passed, so
    /// refreshing cannot succeed and only a new sign-in will help. Unknown
    /// (false) when the credentials don't carry refreshTokenExpiresAt, so an
    /// unfamiliar file shape still gets the normal refresh-then-401 path.
    /// Never throws.</summary>
    public static bool RefreshTokenExpired(ICredentialStore store)
    {
        try
        {
            var expiresAtMs = TryReadCredentials(store)?.OAuth["refreshTokenExpiresAt"]?.GetValue<long>();
            return expiresAtMs is { } ms && DateTimeOffset.FromUnixTimeMilliseconds(ms) <= DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The plan named in the credentials ("pro", "max", "enterprise"...),
    /// or null. Never throws.</summary>
    public static string? ReadSubscriptionType(ICredentialStore store)
    {
        try
        {
            return TryReadCredentials(store)?.OAuth["subscriptionType"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> GetValidAccessTokenAsync(
        HttpClient httpClient, ICredentialStore store, CancellationToken ct)
    {
        var current = TryReadCredentials(store);
        if (current is null)
            return null;
        if (!current.NeedsRefresh || current.RefreshToken is null)
            return current.AccessToken;

        RefreshResult? refreshed;
        try
        {
            refreshed = await RefreshAsync(httpClient, current.RefreshToken, ct);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ClaudeTokenRefresher.GetValidAccessTokenAsync (refresh)", ex);
            return current.AccessToken;
        }
        if (refreshed is null)
            return current.AccessToken; // Refresh failed - the 401 path handles it.

        var latest = TryReadCredentials(store);
        if (latest is not null &&
            (latest.RefreshToken != current.RefreshToken || latest.AccessToken != current.AccessToken))
        {
            // Another process refreshed while our request was in flight. Its
            // tokens are at least as new as ours; don't overwrite them.
            DiagnosticLog.WriteInfo(
                $"ClaudeTokenRefresher: credentials changed in the {store.Description} during refresh; kept the stored tokens.");
            return latest.NeedsRefresh ? refreshed.AccessToken : latest.AccessToken;
        }

        var target = latest ?? current;
        target.OAuth["accessToken"] = refreshed.AccessToken;
        target.OAuth["refreshToken"] = refreshed.RefreshToken ?? current.RefreshToken;
        target.OAuth["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresInSeconds).ToUnixTimeMilliseconds();

        if (store.Write(target.Root.ToJsonString()))
            DiagnosticLog.WriteInfo($"ClaudeTokenRefresher: access token refreshed successfully ({store.Description}).");

        // The new token is valid for this call even if persisting it failed.
        return refreshed.AccessToken;
    }

    private sealed record Credentials(JsonNode Root, JsonNode OAuth, string AccessToken,
        string? RefreshToken, DateTimeOffset? ExpiresAt)
    {
        public bool NeedsRefresh => ExpiresAt is null || ExpiresAt.Value - ExpiryBuffer <= DateTimeOffset.UtcNow;
    }

    private static Credentials? TryReadCredentials(ICredentialStore store)
    {
        try
        {
            var json = store.Read();
            if (json is null)
                return null;

            var root = JsonNode.Parse(json);
            var oauth = root?["claudeAiOauth"];
            var accessToken = oauth?["accessToken"]?.GetValue<string>();
            if (root is null || oauth is null || accessToken is null)
                return null;

            var expiresAtMs = oauth["expiresAt"]?.GetValue<long>();
            return new Credentials(root, oauth, accessToken,
                oauth["refreshToken"]?.GetValue<string>(),
                expiresAtMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ClaudeTokenRefresher.TryReadCredentials", ex);
            return null;
        }
    }

    private sealed record RefreshResult(string AccessToken, string? RefreshToken, long ExpiresInSeconds);

    private static async Task<RefreshResult?> RefreshAsync(HttpClient httpClient, string refreshToken, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = refreshToken,
            client_id = ClientId
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Documented gotcha: this endpoint sits behind Cloudflare bot
            // protection and can reject requests from some IP ranges (e.g.
            // datacenter/CI IPs) with a 403 unrelated to the token itself.
            var preview = json.Length > 300 ? json[..300] : json;
            DiagnosticLog.WriteError("ClaudeTokenRefresher.RefreshAsync",
                new HttpRequestException($"Token refresh returned {(int)response.StatusCode} {response.StatusCode}: {preview}"));
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("access_token", out var accessTokenEl) || accessTokenEl.ValueKind != JsonValueKind.String)
            return null;

        var expiresIn = root.TryGetProperty("expires_in", out var expiresInEl) && expiresInEl.ValueKind == JsonValueKind.Number
            ? expiresInEl.GetInt64()
            : 3600; // Documented default per the community-sourced spec (see class doc).

        var newRefreshToken = root.TryGetProperty("refresh_token", out var refreshTokenEl) && refreshTokenEl.ValueKind == JsonValueKind.String
            ? refreshTokenEl.GetString()
            : null;

        return new RefreshResult(accessTokenEl.GetString()!, newRefreshToken, expiresIn);
    }
}
