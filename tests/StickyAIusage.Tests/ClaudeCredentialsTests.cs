using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>Claude Code's stored sign-in, read and never changed: the fields the usage
/// source needs, expiry rules, the macOS store's fallback, and no renewal code anywhere.</summary>
public class ClaudeCredentialsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static string Json(string? token = "tok", long? expiresAt = null, long? refreshExpiresAt = null, string? plan = "max") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            claudeAiOauth = new Dictionary<string, object?>
            {
                ["accessToken"] = token,
                ["refreshToken"] = "never-used",
                ["expiresAt"] = expiresAt,
                ["refreshTokenExpiresAt"] = refreshExpiresAt,
                ["subscriptionType"] = plan,
                ["scopes"] = new[] { "user:inference" }
            }
        });

    [Fact]
    public void The_fields_the_usage_source_needs_are_read()
    {
        var credentials = ClaudeCredentials.Parse(Json(expiresAt: Now.AddHours(1).ToUnixTimeMilliseconds(),
            refreshExpiresAt: Now.AddDays(30).ToUnixTimeMilliseconds()))!;

        Assert.Equal("tok", credentials.AccessToken);
        Assert.Equal(Now.AddHours(1), credentials.ExpiresAt);
        Assert.Equal(Now.AddDays(30), credentials.RefreshTokenExpiresAt);
        Assert.Equal("max", credentials.SubscriptionType);
        Assert.False(credentials.AccessTokenExpired(Now));
        Assert.False(credentials.SignInExpired(Now));
    }

    [Theory]
    [InlineData(61, false)]
    [InlineData(59, true)]   // within the one-minute margin
    [InlineData(0, true)]
    [InlineData(-600, true)]
    public void A_token_counts_as_expired_from_a_minute_before_its_time(int secondsLeft, bool expired) =>
        Assert.Equal(expired, ClaudeCredentials.Parse(Json(expiresAt: Now.AddSeconds(secondsLeft).ToUnixTimeMilliseconds()))!
            .AccessTokenExpired(Now));

    [Fact]
    public void Without_dates_nothing_counts_as_expired()
    {
        var credentials = ClaudeCredentials.Parse(Json())!;
        Assert.Null(credentials.ExpiresAt);
        Assert.False(credentials.AccessTokenExpired(Now));
        Assert.False(credentials.SignInExpired(Now));
    }

    [Fact]
    public void An_ended_sign_in_is_recognised() =>
        Assert.True(ClaudeCredentials.Parse(Json(refreshExpiresAt: Now.AddMinutes(-1).ToUnixTimeMilliseconds()))!.SignInExpired(Now));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"claudeAiOauth":{}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":42}}""")]
    [InlineData("""{"claudeAiOauth":"x"}""")]
    public void Anything_without_an_access_token_is_no_sign_in(string json) => Assert.Null(ClaudeCredentials.Parse(json));

    [Fact]
    public void Odd_dates_are_ignored_rather_than_failing()
    {
        var credentials = ClaudeCredentials.Parse("""{"claudeAiOauth":{"accessToken":"t","expiresAt":"soon","refreshTokenExpiresAt":-5}}""")!;
        Assert.Null(credentials.ExpiresAt);
        Assert.Null(credentials.RefreshTokenExpiresAt);
    }

    [Fact]
    public void The_token_never_shows_in_text()
    {
        var credentials = ClaudeCredentials.Parse(Json(token: "secret-token-value"))!;
        Assert.DoesNotContain("secret-token-value", credentials.ToString());
        Assert.DoesNotContain("secret-token-value", $"{credentials}");
    }

    [Fact]
    public void A_store_that_fails_reads_as_no_sign_in() =>
        Assert.Null(ClaudeCredentials.Read(new Store(() => throw new IOException("locked"))));

    [Fact]
    public void On_a_Mac_the_Keychain_item_comes_first_then_the_file()
    {
        var keychainReads = 0;
        var fileReads = 0;
        var withItem = new MacCredentialStore(new Store(() => { keychainReads++; return Json(token: "from-keychain"); }, "macOS Keychain"),
            new Store(() => { fileReads++; return Json(token: "from-file"); }, "credentials file"));
        Assert.Equal("from-keychain", ClaudeCredentials.Read(withItem)!.AccessToken);
        Assert.Equal("macOS Keychain", withItem.Description);
        Assert.Equal((1, 0), (keychainReads, fileReads));

        var withoutItem = new MacCredentialStore(new Store(() => null, "macOS Keychain"), new Store(() => Json(token: "from-file"), "credentials file"));
        Assert.Equal("from-file", ClaudeCredentials.Read(withoutItem)!.AccessToken);
        Assert.Equal("credentials file", withoutItem.Description);
    }

    /// <summary>The app never renews the sign-in: no token endpoint, client id or refresh grant in its code.</summary>
    [Fact]
    public void No_code_renews_or_writes_the_sign_in()
    {
        var src = Path.Combine(InstallMethodTests.RepoRoot(), "src");
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => x.line.Contains("refresh_token") || x.line.Contains("oauth/token") || x.line.Contains("9d1c250a") ||
                        x.line.Contains("add-generic-password") || x.line.Contains("grant_type"))
            .Select(x => $"{Path.GetRelativePath(src, x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(hits);
        Assert.DoesNotContain(typeof(ICredentialStore).GetMethods(), m => m.Name.Contains("Write"));
    }

    private sealed class Store(Func<string?> read, string description = "test") : ICredentialStore
    {
        public string Description => description;
        public string? Read() => read();
    }
}
