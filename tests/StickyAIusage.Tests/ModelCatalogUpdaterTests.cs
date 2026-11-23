using System.Net;
using System.Net.Http;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>The daily price-list refresh: when it asks, what it keeps, and
/// that nothing asks GitHub while Check for updates is off.</summary>
public class ModelCatalogUpdaterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeGitHub _github = new();
    private readonly List<string> _log = [];
    private readonly List<ModelCatalog> _applied = [];
    private DateTimeOffset _now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private ModelCatalogUpdater Updater(ModelCatalog? inUse = null) =>
        new(new HttpClient(_github), _dir.Path, () => _now)
        {
            Log = _log.Add,
            Apply = _applied.Add,
            // A list in use of its own (dated 2026-11-17, the clock's day), so these dates don't move when the
            // repository's price list gets a new date.
            InUse = () => inUse ?? ListInUse
        };

    private static readonly ModelCatalog ListInUse = ModelCatalogTests.Parse(ModelCatalogTests.MinimalJson(updated: "2026-11-17"));

    private string CachedFile => _dir.Combine(ModelCatalog.CacheFileName);

    [Fact]
    public async Task A_valid_newer_list_is_saved_and_used()
    {
        _github.Respond(HttpStatusCode.OK, ModelCatalogTests.MinimalJson(updated: "2026-11-18"));

        var outcome = await Updater().RefreshAsync(CancellationToken.None);

        Assert.Equal(RefreshOutcome.Updated, outcome);
        Assert.Equal(new DateOnly(2026, 11, 18), Assert.Single(_applied).Updated);
        Assert.Equal("downloaded", _applied[0].Origin);
        Assert.Equal(new DateOnly(2026, 11, 18), ModelCatalog.ReadCached(_dir.Path)!.Updated);
        Assert.Equal(ModelCatalogUpdater.DefaultSource, _github.Requests.Single());
        Assert.Contains("updated to the published price list of 2026-11-18", _log.Single());
    }

    [Fact]
    public async Task A_list_dated_after_tomorrow_is_refused()
    {
        // It would outrank every later built-in list for good.
        _github.Respond(HttpStatusCode.OK, ModelCatalogTests.MinimalJson(updated: "2026-11-19"));

        Assert.Equal(RefreshOutcome.Invalid, await Updater().RefreshAsync(CancellationToken.None));
        Assert.Empty(_applied);
        Assert.False(File.Exists(CachedFile));
        Assert.Contains("in the future", _log.Single());
    }

    [Fact]
    public void A_downloaded_file_dated_in_the_future_is_ignored_at_start()
    {
        File.WriteAllText(CachedFile, ModelCatalogTests.MinimalJson(updated: "2099-01-01"));
        Assert.Null(ModelCatalog.ReadCached(_dir.Path));
    }

    [Fact]
    public void After_dispose_a_due_check_starts_nothing()
    {
        var updater = Updater();
        _now += ModelCatalogUpdater.StartDelay;
        Assert.True(updater.IsDue());

        updater.Dispose();
        updater.RefreshIfDue();
        updater.Dispose(); // twice is harmless

        Assert.Empty(_github.Requests);
    }

    [Theory]
    [InlineData("2026-11-17")] // the same day as the list in use
    [InlineData("2026-01-01")]
    public async Task An_older_or_same_day_list_changes_nothing(string updated)
    {
        _github.Respond(HttpStatusCode.OK, ModelCatalogTests.MinimalJson(updated: updated));

        Assert.Equal(RefreshOutcome.NotNewer, await Updater().RefreshAsync(CancellationToken.None));
        Assert.Empty(_applied);
        Assert.False(File.Exists(CachedFile));
    }

    [Theory]
    [InlineData("{ \"schema\": 1 }")]
    [InlineData("<html>Not found</html>")]
    [InlineData("")]
    public async Task An_invalid_list_changes_nothing(string body)
    {
        _github.Respond(HttpStatusCode.OK, body);

        Assert.Equal(RefreshOutcome.Invalid, await Updater().RefreshAsync(CancellationToken.None));
        Assert.Empty(_applied);
        Assert.False(File.Exists(CachedFile));
        Assert.Contains("Keeping the prices in use", _log.Single());
    }

    [Fact]
    public async Task A_list_with_prices_out_of_range_changes_nothing()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ModelCatalogTests.MinimalJson(updated: "2026-11-18"))!;
        json["models"]![0]!["output"] = 5000;
        _github.Respond(HttpStatusCode.OK, json.ToJsonString());

        Assert.Equal(RefreshOutcome.Invalid, await Updater().RefreshAsync(CancellationToken.None));
        Assert.Empty(_applied);
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_not_read()
    {
        _github.Respond(HttpStatusCode.OK, ModelCatalogTests.MinimalJson(updated: "2026-11-18") + new string(' ', ModelCatalog.MaxBytes));

        Assert.Equal(RefreshOutcome.Invalid, await Updater().RefreshAsync(CancellationToken.None));
        Assert.Empty(_applied);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_download_changes_nothing(HttpStatusCode status)
    {
        _github.Respond(status, "nope");

        Assert.Equal(RefreshOutcome.Failed, await Updater().RefreshAsync(CancellationToken.None));
        Assert.Empty(_applied);
        Assert.Contains($"HTTP {(int)status}", _log.Single());
    }

    [Fact]
    public async Task A_network_error_changes_nothing_and_does_not_throw()
    {
        _github.Throw = true;

        Assert.Equal(RefreshOutcome.Failed, await Updater().RefreshAsync(CancellationToken.None));
        Assert.Empty(_applied);
    }

    [Fact]
    public async Task It_asks_at_most_once_a_day_and_not_in_the_first_minute_even_across_restarts()
    {
        _github.Respond(HttpStatusCode.NotFound, "");
        var updater = Updater();
        Assert.False(updater.IsDue()); // just started

        _now += ModelCatalogUpdater.StartDelay;
        Assert.True(updater.IsDue());
        await updater.RefreshAsync(CancellationToken.None);
        Assert.False(updater.IsDue());

        // A restart an hour later reads the stamp: still not due.
        _now += TimeSpan.FromHours(1);
        var restarted = Updater();
        _now += ModelCatalogUpdater.StartDelay;
        Assert.False(restarted.IsDue());

        _now += TimeSpan.FromHours(23);
        Assert.True(restarted.IsDue());
        Assert.Single(_github.Requests);
    }

    [Fact]
    public async Task A_clock_set_back_makes_it_due_again()
    {
        _github.Respond(HttpStatusCode.NotFound, "");
        _now += ModelCatalogUpdater.StartDelay;
        await Updater().RefreshAsync(CancellationToken.None);

        _now -= TimeSpan.FromDays(3);
        var updater = Updater();
        _now += ModelCatalogUpdater.StartDelay;
        Assert.True(updater.IsDue());
    }

    [Fact]
    public void A_refresh_that_is_not_due_sends_nothing()
    {
        var updater = Updater();
        updater.RefreshIfDue(); // the first minute
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task RefreshIfDue_runs_one_check_in_the_background()
    {
        _github.Respond(HttpStatusCode.OK, ModelCatalogTests.MinimalJson(updated: "2026-11-18"));
        var updater = Updater();
        _now += ModelCatalogUpdater.StartDelay;

        updater.RefreshIfDue();
        updater.RefreshIfDue();
        for (var i = 0; i < 100 && _applied.Count == 0; i++)
            await Task.Delay(20);

        Assert.Single(_applied);
        Assert.Single(_github.Requests);
    }

    // ------------------------------------------------------- the switch

    private static (ModelCatalogUpdater, IReadOnlyList<IDisposable>) NeverBuilt(Func<DateTimeOffset> _) =>
        throw new InvalidOperationException("No price check may be built while Check for updates is off.");

    private static (IClaudeUsageSource, IReadOnlyList<IDisposable>) NoEndpoint(Func<DateTimeOffset> _) =>
        throw new InvalidOperationException("Not used by these tests.");

    [Fact]
    public async Task With_Check_for_updates_off_nothing_asks_GitHub()
    {
        var settings = new AppSettings(); // a new install: off
        Assert.False(settings.CheckForUpdates);

        using var services = AppServices.Create(settings, new ClaudeSessionLogSource(_dir.Combine("projects")),
            _dir.Combine("feed"), watchFeed: false, NoEndpoint, NeverBuilt);
        await services.Feed.GetSummaryAsync();

        Assert.False(services.ChecksForUpdates);
    }

    [Fact]
    public async Task With_Check_for_updates_on_each_tick_asks_the_check_whether_it_is_due()
    {
        _github.Respond(HttpStatusCode.NotFound, "");
        var built = 0;
        using var services = AppServices.Create(new AppSettings { CheckForUpdates = true },
            new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false, NoEndpoint,
            clock =>
            {
                built++;
                var updater = Updater();
                return (updater, [updater]);
            });
        _now += ModelCatalogUpdater.StartDelay;

        await services.Feed.GetSummaryAsync();
        for (var i = 0; i < 100 && _github.Requests.Count == 0; i++)
            await Task.Delay(20);

        Assert.Equal(1, built);
        Assert.True(services.ChecksForUpdates);
        Assert.Single(_github.Requests);
    }

    [Fact]
    public void Turning_Check_for_updates_on_or_off_in_Settings_rebuilds_the_services()
    {
        var settings = new AppSettings();
        using var host = new AppServicesHost(settings, s => AppServices.Create(s,
            new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false, NoEndpoint));

        Assert.False(host.SettingsChanged());
        settings.CheckForUpdates = true;
        Assert.True(host.SettingsChanged());
        Assert.True(host.ApplyPendingChange());
        Assert.True(host.Current.ChecksForUpdates);
        Assert.False(host.SettingsChanged());
    }

    [Fact]
    public void Check_for_updates_round_trips_through_the_settings_file()
    {
        var path = _dir.Combine("settings.json");
        Assert.False(SettingsService.Load(path).CheckForUpdates);
        SettingsService.Save(new AppSettings { CheckForUpdates = true }, path);
        Assert.True(SettingsService.Load(path).CheckForUpdates);
    }

    /// <summary>Answers every request with one canned response and records the URLs.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.NotFound;
        private string _body = "";

        public List<Uri> Requests { get; } = [];
        public bool Throw { get; set; }

        public void Respond(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!);
            if (Throw)
                throw new HttpRequestException("No network");
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }
}
