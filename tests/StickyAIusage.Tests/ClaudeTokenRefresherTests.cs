using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class ClaudeTokenRefresherTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string CredPath => _dir.Combine(".credentials.json");

    public void Dispose() => _dir.Dispose();

    private sealed class FakeTokenEndpoint(HttpStatusCode status, string body, Action? beforeResponding = null)
        : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            beforeResponding?.Invoke();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static readonly string NewTokensBody =
        """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""";

    private void WriteCredentials(string access, string refresh, DateTimeOffset expiresAt,
        DateTimeOffset? refreshTokenExpiresAt = null)
    {
        var oauth = new Dictionary<string, object>
        {
            ["accessToken"] = access,
            ["refreshToken"] = refresh,
            ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
            ["scopes"] = new[] { "user:inference", "user:profile" },
            ["subscriptionType"] = "enterprise"
        };
        if (refreshTokenExpiresAt is { } rt)
            oauth["refreshTokenExpiresAt"] = rt.ToUnixTimeMilliseconds();

        File.WriteAllText(CredPath, JsonSerializer.Serialize(new
        {
            claudeAiOauth = oauth,
            someOtherTool = new { keep = true }
        }));
    }

    private JsonNode ReadCredentials() => JsonNode.Parse(File.ReadAllText(CredPath))!;

    [Fact]
    public async Task Unexpired_token_is_returned_without_calling_the_endpoint()
    {
        WriteCredentials("still-valid", "rt", DateTimeOffset.UtcNow.AddMinutes(30));
        var endpoint = new FakeTokenEndpoint(HttpStatusCode.OK, NewTokensBody);

        var token = await ClaudeTokenRefresher.GetValidAccessTokenAsync(new HttpClient(endpoint), CredPath, default);

        Assert.Equal("still-valid", token);
        Assert.Empty(endpoint.RequestBodies);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_and_written_back_preserving_other_fields()
    {
        WriteCredentials("expired", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5));
        var endpoint = new FakeTokenEndpoint(HttpStatusCode.OK, NewTokensBody);

        var token = await ClaudeTokenRefresher.GetValidAccessTokenAsync(new HttpClient(endpoint), CredPath, default);

        Assert.Equal("new-access", token);
        var request = JsonNode.Parse(Assert.Single(endpoint.RequestBodies))!;
        Assert.Equal("refresh_token", request["grant_type"]!.GetValue<string>());
        Assert.Equal("old-refresh", request["refresh_token"]!.GetValue<string>());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", request["client_id"]!.GetValue<string>());

        var saved = ReadCredentials();
        var oauth = saved["claudeAiOauth"]!;
        Assert.Equal("new-access", oauth["accessToken"]!.GetValue<string>());
        Assert.Equal("new-refresh", oauth["refreshToken"]!.GetValue<string>());
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(oauth["expiresAt"]!.GetValue<long>());
        Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
        Assert.Equal("enterprise", oauth["subscriptionType"]!.GetValue<string>());
        Assert.Equal(2, oauth["scopes"]!.AsArray().Count);
        Assert.True(saved["someOtherTool"]!["keep"]!.GetValue<bool>());
        Assert.False(File.Exists(CredPath + ".stickyaiusage.tmp"));
    }

    [Fact]
    public async Task Tokens_written_by_another_process_during_refresh_are_not_overwritten()
    {
        WriteCredentials("expired", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5));
        // Claude Code refreshes the same file while our request is in flight.
        var endpoint = new FakeTokenEndpoint(HttpStatusCode.OK, NewTokensBody,
            beforeResponding: () => WriteCredentials("claude-code-access", "claude-code-refresh", DateTimeOffset.UtcNow.AddHours(1)));

        var token = await ClaudeTokenRefresher.GetValidAccessTokenAsync(new HttpClient(endpoint), CredPath, default);

        Assert.Equal("claude-code-access", token);
        var oauth = ReadCredentials()["claudeAiOauth"]!;
        Assert.Equal("claude-code-access", oauth["accessToken"]!.GetValue<string>());
        Assert.Equal("claude-code-refresh", oauth["refreshToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_refresh_returns_the_old_token_and_leaves_the_file_untouched()
    {
        WriteCredentials("expired", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5));
        var before = File.ReadAllText(CredPath);
        var endpoint = new FakeTokenEndpoint(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var token = await ClaudeTokenRefresher.GetValidAccessTokenAsync(new HttpClient(endpoint), CredPath, default);

        Assert.Equal("expired", token);
        Assert.Equal(before, File.ReadAllText(CredPath));
    }

    [Theory]
    [InlineData(-5, true)]   // the refresh token's own expiry has passed
    [InlineData(60, false)]  // still good
    public void An_expired_refresh_token_is_recognised_without_calling_the_endpoint(int minutes, bool expired)
    {
        WriteCredentials("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1),
            refreshTokenExpiresAt: DateTimeOffset.UtcNow.AddMinutes(minutes));

        Assert.Equal(expired, ClaudeTokenRefresher.RefreshTokenExpired(new FileCredentialStore(CredPath)));
    }

    [Fact]
    public void Credentials_without_a_refresh_token_expiry_are_left_to_the_normal_path()
    {
        WriteCredentials("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(ClaudeTokenRefresher.RefreshTokenExpired(new FileCredentialStore(CredPath)));
    }

    [Fact]
    public void Rewritten_credentials_file_is_readable_only_by_its_owner()
    {
        File.WriteAllText(CredPath, "{}");
        File.WriteAllText(CredPath + ".stickyaiusage.tmp", "leftover");

        Assert.True(new FileCredentialStore(CredPath).Write("""{"a":1}"""));

        Assert.Equal("""{"a":1}""", File.ReadAllText(CredPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CredPath));
    }

    [Fact]
    public async Task Missing_credentials_file_returns_null()
    {
        var endpoint = new FakeTokenEndpoint(HttpStatusCode.OK, NewTokensBody);
        Assert.Null(await ClaudeTokenRefresher.GetValidAccessTokenAsync(new HttpClient(endpoint), CredPath, default));
    }
}
