using Jularr.Web.Features.Monitoring;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The Movie and Series pages render the Instant Play controls of docs/mockups/instant-play: the in-place playback action with its
/// script-free fallback, the compact action of episode rows, the request state in consumer words, no player word on a manager-only
/// instance, and the contract between the page, instant-play.js and the client API it calls.
/// </summary>
[TestClass]
public sealed partial class InstantPlayPageMarkupTests
{
    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title, string tmdbId)
    {
        var work = await new LibraryCanonicalSeed(host.Db).AddWorkAsync(type, title, 2024);
        host.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = type, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true });
        await host.Db.SaveChangesAsync();
        return work;
    }

    private static async Task<VideoDetailPageTestHost> ReadyHostAsync()
    {
        var host = await VideoDetailPageTestHost.CreateAsync();
        await host.MakeAcquisitionReadyAsync();
        return host;
    }

    private static string Hero(string html)
    {
        var from = html.IndexOf("<section class=\"ad-hero", StringComparison.Ordinal);
        return html[from..html.IndexOf("</section>", from, StringComparison.Ordinal)];
    }

    /// <summary>What a visitor reads: the page without scripts, styles and tags.</summary>
    private static string VisibleText(string html)
    {
        var withoutCode = ScriptOrStyle().Replace(html, " ");
        return Whitespace().Replace(Tags().Replace(withoutCode, " "), " ");
    }

    private static string MoviePayload(Guid workId, string extra = "") =>
        $$"""{"workId":"{{workId}}","title":"Film","year":2024{{extra}} }""";

    private static Task<AcquisitionRequest> OpenRequestAsync(VideoDetailPageTestHost host, MediaAcquisitionKind kind, string tmdbId, AcquisitionRequestStatus status, string? payload = null) =>
        new AcquisitionAccessStore(host.Db).CreateAsync(new AcquisitionRequestDraft(kind, "tmdb", tmdbId, "Title", null, null, payload), "someone-else", status, "owner", CancellationToken.None);

    [TestMethod]
    public async Task AMissingMovieOffersTheMorphingWatchNowControlThatStillPostsWithoutScript()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true);

        var hero = Hero(html);
        StringAssert.Contains(hero, "data-instant-play");
        StringAssert.Contains(hero, "data-ip-unit=\"movie\"");
        StringAssert.Contains(hero, $"data-ip-work-id=\"{movie.Id}\"");
        StringAssert.Contains(hero, $"data-ip-watch-href=\"/Library/Watch/{movie.Id}\"");
        StringAssert.Contains(hero, $"data-ip-intent-url=\"{ClientApiPlaybackIntentRoutes.Intents}\"");
        StringAssert.Contains(hero, $"data-ip-status-url=\"{ClientApiPlaybackIntentRoutes.RequestStatus}\"");
        StringAssert.Contains(hero, $"action=\"/Library/Movie/{movie.Id}?handler=Start\"", "Without script the form is the intent: a round trip to the page handler.");
        StringAssert.Contains(hero, "method=\"post\"");
        StringAssert.Contains(hero, "__RequestVerificationToken");
        StringAssert.Contains(hero, ">Watch now<");
        foreach (var hidden in new[] { "data-ip-stop hidden", "data-ip-details hidden", "data-ip-notice hidden", "data-ip-progress hidden" })
        {
            StringAssert.Contains(hero, hidden, "Parts that need script stay hidden without it.");
        }

        StringAssert.Contains(html, "/css/instant-play.css");
        StringAssert.Contains(html, "/js/instant-play.js");
        StringAssert.Contains(html, "id=\"instant-play-text\"");
        StringAssert.Contains(html, "Looking for media");
        StringAssert.Contains(html, "Stop waiting");
    }

    [TestMethod]
    public async Task AMissingMovieThatMayBeWatchedNowStillOffersTheExplicitRequestInTheOverflowMenu()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true);

        var hero = Hero(html);
        StringAssert.Contains(hero, ">Watch now<");
        var menu = Regex.Match(hero, "<details class=\"ad-menu\" data-ad-menu>\\s*<summary[^>]*aria-label=\"More actions\".*?</details>", RegexOptions.Singleline).Value;
        StringAssert.Contains(menu, "data-dc-card-request");
        StringAssert.Contains(VisibleText(menu), "Request");
        Assert.IsFalse(menu.Contains("data-dc-preselect", StringComparison.Ordinal), "A Movie has no episodes to preselect.");
        StringAssert.Contains(html, "data-request-host", "The page carries the shared Request dialog the menu entry opens.");
        StringAssert.Contains(html, "data-dc-external-id=\"603\"");
    }

    [TestMethod]
    public async Task ARequestedMovieOffersNeitherARequestEntryNorADuplicate()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        await OpenRequestAsync(host, MediaAcquisitionKind.Movie, "603", AcquisitionRequestStatus.Pending, MoviePayload(movie.Id));

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true);

        Assert.IsFalse(html.Contains("data-dc-card-request", StringComparison.Ordinal), "An open request is shown, never requested again.");
        Assert.IsFalse(html.Contains("data-request-host", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ANoticeSaysItsHintInsideTheStatusAndOffersOnlyActionsThatAddSomething()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true);

        var hero = Hero(html);
        StringAssert.Contains(hero, "data-ip-pill-hint hidden", "The hint of a notice sits under its title in the status itself, so the status keeps its size.");
        var actions = Regex.Matches(hero, "data-ip-action=\"([a-z]+)\"").Select(match => match.Groups[1].Value).ToArray();
        CollectionAssert.AreEquivalent(new[] { "retry", "view", "back" }, actions, "Retry, View details and Back: \"We'll keep looking\" is a hint, not a second button.");
        Assert.IsFalse(html.Contains("Keep looking", StringComparison.Ordinal));
        StringAssert.Contains(hero, "data-ip-requests-href=\"/Requests\"", "The script points View details at the request this page made.");
    }

    [TestMethod]
    public async Task AMovieThatNeedsApprovalOrIsOnAManagerOnlyInstanceOffersRequestAsItsPrimaryActionWithoutAnOverflowEntry()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        await host.Modules.SetAsync(InstanceModule.Playback, false);

        var hero = Hero(await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true));

        Assert.AreEqual(1, Regex.Matches(hero, "data-dc-card-request").Count, "The primary Request action alone opens the dialog.");
        StringAssert.Contains(VisibleText(hero), "Request");
        Assert.IsFalse(hero.Contains("data-instant-play", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("Watch now", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("/Library/Watch/", StringComparison.Ordinal), "No Player anywhere on a manager-only instance.");
    }

    [TestMethod]
    public async Task TheMorphControlIsAccessibleByConstruction()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        var hero = Hero(await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true));

        StringAssert.Contains(hero, "data-ip-button");
        StringAssert.Contains(hero, "aria-disabled=\"false\"");
        StringAssert.Contains(hero, "aria-busy=\"false\"");
        Assert.IsTrue(Regex.IsMatch(hero, "<p class=\"sr-only\" role=\"status\" aria-live=\"polite\" data-ip-live>"), "State changes are announced politely, not per percent.");
        Assert.IsTrue(Regex.IsMatch(hero, "<svg class=\"ip-ring\"[^>]*focusable=\"false\""), "The spinner is decorative.");
        Assert.IsFalse(hero.Contains("<button type=\"submit\" class=\"ad-primary ip-button\" disabled", StringComparison.Ordinal), "A working action keeps focus: aria-disabled, never disabled.");

        var css = File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "css", "instant-play.css"));
        StringAssert.Contains(css, "prefers-reduced-motion: reduce");
        StringAssert.Contains(css, ":focus-visible");
        Assert.IsFalse(css.Contains("!important", StringComparison.Ordinal));
        Assert.IsFalse(Regex.IsMatch(css, "#[0-9a-fA-F]{3,8}[^0-9a-fA-F]|rgb[(]"), "Colours come from the theme and hero tokens, never from literals.");
    }

    [TestMethod]
    public async Task ASeriesTargetsItsNextEpisodeAndEveryMissingRowOffersItsOwnIntent()
    {
        await using var host = await ReadyHostAsync();
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");
        var seed = new LibraryCanonicalSeed(host.Db);
        var first = await seed.AddEpisodeAsync(series, 1, 1);
        var second = await seed.AddEpisodeAsync(series, 1, 2);
        var third = await seed.AddEpisodeAsync(series, 1, 3);
        await seed.AddVideoAsync(series, third);

        var html = await host.GetOkAsync($"/Library/Series/{series.Id}", asOwner: true);

        var hero = Hero(html);
        StringAssert.Contains(hero, "data-ip-unit=\"episode\"");
        StringAssert.Contains(hero, $"data-ip-episode-id=\"{first.Id}\"", "Start watching targets the next required episode, not the Series.");
        StringAssert.Contains(hero, ">Start watching<");
        StringAssert.Contains(hero, "Season 1 · Episode 1", "The button says which episode it starts on a second line.");
        StringAssert.Contains(hero, "has-context");
        Assert.AreEqual(3, Regex.Matches(html, "data-instant-play(?![-a-z])").Count, "The hero and the two missing rows, not the local episode.");
        foreach (var row in new[] { first, second })
        {
            StringAssert.Contains(html, $"data-ip-episode-id=\"{row.Id}\"");
            StringAssert.Contains(html, $"<input type=\"hidden\" name=\"episodeId\" value=\"{row.Id}\" />");
        }

        Assert.IsFalse(html.Contains($"data-ip-episode-id=\"{third.Id}\"", StringComparison.Ordinal), "A local episode plays; it has no acquisition control.");
        Assert.AreEqual(2, Regex.Matches(html, "ip-action is-compact").Count);
        Assert.IsFalse(VisibleText(html).Contains("Not available", StringComparison.Ordinal), "An episode that may be watched now does not also say it is not available.");
    }

    [TestMethod]
    public async Task APlayableTitleHasNoAcquisitionControl()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        await new LibraryCanonicalSeed(host.Db).AddVideoAsync(movie, null);

        var hero = Hero(await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true));

        StringAssert.Contains(hero, $"href=\"/Library/Watch/{movie.Id}\"");
        Assert.IsFalse(hero.Contains("data-instant-play", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnOpenRequestIsShownInConsumerWordsAndOnlyWorkingStatesAreFollowedLive()
    {
        await using var host = await ReadyHostAsync();
        // A search that found nothing and is scheduled again reads "Not available yet"; every other open state is working or waiting.
        var cases = new (string TmdbId, AcquisitionRequestStatus Status, bool BackedOff, string Text, bool Live)[]
        {
            ("1", AcquisitionRequestStatus.Pending, false, "Waiting for approval", false),
            ("2", AcquisitionRequestStatus.Approved, false, "Looking for media", true),
            ("3", AcquisitionRequestStatus.Searching, false, "Looking for media", true),
            ("4", AcquisitionRequestStatus.Importing, false, "Preparing", true),
            ("5", AcquisitionRequestStatus.Approved, true, "Not available yet", false)
        };
        foreach (var item in cases)
        {
            var movie = await AddTitleAsync(host, WorkMediaType.Movie, $"Film {item.TmdbId}", item.TmdbId);
            var payload = MoviePayload(movie.Id, item.BackedOff ? ",\"searches\":3,\"nextSearchUtc\":\"2099-01-01T00:00:00Z\"" : "");
            var request = await OpenRequestAsync(host, MediaAcquisitionKind.Movie, item.TmdbId, item.Status, payload);

            var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true);

            var hero = Hero(html);
            var pill = Regex.Match(hero, "<a class=\"ad-secondary ip-observe\"[^>]*>.*?</a>", RegexOptions.Singleline).Value;
            StringAssert.Contains(VisibleText(pill), item.Text, item.Status.ToString());
            Assert.AreEqual(item.Live, pill.Contains("data-instant-play-observe", StringComparison.Ordinal), item.Status.ToString());
            Assert.AreEqual(item.Live, pill.Contains($"data-ip-request-id=\"{request.Id}\"", StringComparison.Ordinal), item.Status.ToString());
            StringAssert.Contains(pill, $"href=\"/Requests/{request.Id}\"", "The pill opens the request it speaks of, not the list of every request.");
            var text = VisibleText(html);
            foreach (var technical in new[] { "Downloading", "Importing", "Searching", "Cancel download" })
            {
                Assert.IsFalse(text.Contains(technical, StringComparison.Ordinal), $"{item.Status}: '{technical}' is not consumer wording.");
            }
        }
    }

    [TestMethod]
    public async Task ATransferWithATrustworthySizeShowsItsPercentageAndOneWithoutDoesNot()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var other = await AddTitleAsync(host, WorkMediaType.Movie, "Silent Orbit", "700");
        var store = new OperationStore(host.Db);
        var sized = await store.CreateAsync(new OperationDescriptor("video-usenet-download", "External downloads", "Moon Empire", IsDownload: true, BytesTotal: 1_000));
        await store.MarkRunningAsync(sized);
        await store.ReportProgressAsync(sized, 90, bytesCompleted: 420, bytesTotal: 1_000);
        var bare = await store.CreateAsync(new OperationDescriptor("video-usenet-download", "External downloads", "Silent Orbit", IsDownload: true));
        await store.MarkRunningAsync(bare);
        await store.ReportProgressAsync(bare, 55);
        foreach (var (title, tmdb, operation) in new[] { (movie, "603", sized), (other, "700", bare) })
        {
            var request = await OpenRequestAsync(host, MediaAcquisitionKind.Movie, tmdb, AcquisitionRequestStatus.Downloading, MoviePayload(title.Id));
            await host.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AcquisitionRequests\" SET \"OperationId\" = {operation.ToString()} WHERE \"Id\" = {request.Id.ToString()}");
        }

        var withPercent = VisibleText(Hero(await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true)));
        var withoutPercent = VisibleText(Hero(await host.GetOkAsync($"/Library/Movie/{other.Id}", asOwner: true)));

        StringAssert.Contains(withPercent, "Getting movie · 42%");
        StringAssert.Contains(withoutPercent, "Getting movie");
        Assert.IsFalse(withoutPercent.Contains('%'), "A percentage the transfer did not report is never shown.");
    }

    [TestMethod]
    public async Task AProfileThatCannotReadTheRequestIsNeverGivenAPollThatWouldAnswer404()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        await OpenRequestAsync(host, MediaAcquisitionKind.Movie, "603", AcquisitionRequestStatus.Approved);
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Browse);

        var hero = Hero(await host.GetOkAsync($"/Library/Movie/{movie.Id}"));

        Assert.IsFalse(hero.Contains("ip-observe", StringComparison.Ordinal), "A profile that may not read the request is told nothing of it.");
        Assert.IsFalse(VisibleText(hero).Contains("Looking for media", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("data-instant-play", StringComparison.Ordinal), "The status read answers 404 for this profile, so the page does not poll it.");
    }

    [TestMethod]
    public async Task AManagerOnlyInstanceCarriesNoWordAndNoControlOfPlaying()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");
        var episode = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 1);
        await OpenRequestAsync(host, MediaAcquisitionKind.Movie, "603", AcquisitionRequestStatus.Importing);
        await host.Modules.SetAsync(InstanceModule.Playback, false);

        foreach (var path in new[] { $"/Library/Movie/{movie.Id}", $"/Library/Series/{series.Id}" })
        {
            var html = await host.GetOkAsync(path, asOwner: true);
            var page = path.Contains("Movie", StringComparison.Ordinal) ? "Movie" : "Series";

            Assert.IsFalse(Regex.IsMatch(html, "data-instant-play(?![-a-z])"), $"{page} has no playback intent control.");
            foreach (var word in new[] { "Starting playback", "Ready to watch", "Watch now", "Start watching", "Stop waiting", "/Library/Watch", "acquisition.playback." })
            {
                Assert.IsFalse(html.Contains(word, StringComparison.Ordinal), $"{page} must not carry '{word}', not even inside its script data.");
            }
        }

        var movieHtml = await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true);
        StringAssert.Contains(VisibleText(Hero(movieHtml)), "Preparing");
        Assert.IsNotNull(episode);
    }

    [TestMethod]
    public async Task AnAvailableManagerOnlyTitleWithMonitoringShowsBothStatesWithoutAPlayer()
    {
        await using var host = await ReadyHostAsync();
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Moon Empire", "603");
        var seed = new LibraryCanonicalSeed(host.Db);
        var episode = await seed.AddEpisodeAsync(series, 1, 1);
        await seed.AddVideoAsync(series, episode);
        var payload = MoviePayload(series.Id, ",\"nextSearchUtc\":\"2099-01-01T00:00:00Z\"");
        await OpenRequestAsync(host, MediaAcquisitionKind.Tv, "603", AcquisitionRequestStatus.Approved, payload);
        await MonitoringTestSupport.Commands(host.Db).SetAsync(MonitoringTargetKind.Work, series.Id, true, CancellationToken.None);
        await host.Modules.SetAsync(InstanceModule.Playback, false);

        var hero = VisibleText(Hero(await host.GetOkAsync($"/Library/Series/{series.Id}", asOwner: true)));

        StringAssert.Contains(hero, "Available");
        StringAssert.Contains(hero, "Monitoring future releases");
        Assert.IsFalse(hero.Contains("Play", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheScriptIsGivenOnlyTheWordsItRendersAndEveryKeyItUsesIsInTheCatalog()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true);
        var json = Regex.Match(html, "<script type=\"application/json\" id=\"instant-play-text\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        var shipped = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        var source = File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "instant-play.js"));

        var used = Regex.Matches(source, "\"((?:acquisition\\.(?:state|playback|instant))\\.[A-Za-z.]+)\"").Select(x => x.Groups[1].Value).Distinct().ToArray();

        Assert.IsTrue(used.Length > 25);
        foreach (var key in used)
        {
            Assert.IsTrue(UiTranslationResources.TryGet(key, out _), $"{key} is not in the catalog.");
            Assert.IsTrue(shipped.ContainsKey(key), $"{key} is used by instant-play.js but not shipped to it.");
        }

        CollectionAssert.AreEquivalent(
            shipped.Keys.Where(x => x.StartsWith("acquisition.", StringComparison.Ordinal)).ToArray(),
            shipped.Keys.ToArray(),
            "Nothing but the acquisition vocabulary is shipped.");
    }

    [TestMethod]
    public async Task TheScriptConsumesExactlyTheFieldsAndValuesTheClientApiSends()
    {
        await using var host = await ReadyHostAsync();
        var source = File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "instant-play.js"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var view = new ConsumerAcquisitionView(ConsumerAcquisitionState.GettingMedia, ConsumerMediaUnit.Episode, 42, true);
        var target = new ClientVideoTarget(Guid.NewGuid(), Guid.NewGuid());
        var intent = JsonDocument.Parse(JsonSerializer.Serialize(new ClientPlaybackIntentResponse(PlaybackIntentOutcome.Acquiring, target, Guid.NewGuid(), view), options)).RootElement;
        var status = JsonDocument.Parse(JsonSerializer.Serialize(new ClientRequestStatusResponse(Guid.NewGuid(), new ClientVideoTarget(Guid.NewGuid(), null), view), options)).RootElement;

        CollectionAssert.AreEquivalent(new[] { "outcome", "target", "requestId", "acquisition" }, intent.EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "requestId", "target", "acquisition" }, status.EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "state", "mediaUnit", "progressPercent", "isMonitoring" }, intent.GetProperty("acquisition").EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.IsSubsetOf(new[] { "workId", "workEpisodeId" }, intent.GetProperty("target").EnumerateObject().Select(x => x.Name).ToArray());
        foreach (var field in new[] { "outcome", "requestId", "acquisition", "target?.workEpisodeId", "progressPercent", "mediaUnit" })
        {
            StringAssert.Contains(source, field, $"instant-play.js does not read '{field}'.");
        }

        foreach (var state in Enum.GetValues<ConsumerAcquisitionState>())
        {
            var name = JsonSerializer.Serialize(state, options).Trim('"');
            StringAssert.Contains(source, $"\"{name}\"", $"instant-play.js does not know the state {name}.");
        }

        foreach (var outcome in Enum.GetValues<PlaybackIntentOutcome>())
        {
            var name = JsonSerializer.Serialize(outcome, options).Trim('"');
            StringAssert.Contains(source, $"\"{name}\"", $"instant-play.js does not handle the outcome {name}.");
        }

        foreach (var unit in Enum.GetValues<ConsumerMediaUnit>())
        {
            StringAssert.Contains(source, JsonSerializer.Serialize(unit, options).Trim('"'));
        }

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(x => x.RoutePattern.RawText).ToArray();
        CollectionAssert.Contains(endpoints, ClientApiPlaybackIntentRoutes.Intents);
        CollectionAssert.Contains(endpoints, ClientApiPlaybackIntentRoutes.RequestStatus.Replace("{id}", "{requestId:guid}", StringComparison.Ordinal));
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"/api/client/v1/requests/{Guid.NewGuid()}", asOwner: true)).Status);
    }

    [GeneratedRegex("<(script|style)\\b.*?</\\1>", RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
