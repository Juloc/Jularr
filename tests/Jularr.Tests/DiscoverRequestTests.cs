using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DiscoverIndexModel = Jularr.Web.Pages.Discover.IndexModel;

namespace Jularr.Tests;

/// <summary>
/// The Discover Request dialog end to end on the server: one Request action for every capability, scope that only
/// series have and that is validated against the selected Work's own structure, language for anime, and the states
/// the dialog shows (resolved settings, success, already requested, forbidden).
/// </summary>
[TestClass]
public sealed class DiscoverRequestTests
{
    private const string Alice = "alice";
    private const string BreakingBad = "1396";
    private const string OtherSeries = "1399";
    private const string Movie = "550";

    [TestMethod]
    public async Task RequestAndInstantCapabilitiesBothOfferTheSameRequestAction()
    {
        await using var host = await RequestHost.CreateAsync();
        await host.Fixture.Capabilities.SetUserOverrideAsync(Alice, WorkMediaType.Anime, MediaCapability.Request);
        await host.Fixture.Capabilities.SetUserOverrideAsync("bob", WorkMediaType.Anime, MediaCapability.Instant);

        var requester = host.Page(Alice);
        var instant = host.Page("bob");
        await requester.OnGetAsync(CancellationToken.None);
        await instant.OnGetAsync(CancellationToken.None);

        Assert.IsTrue(requester.RequestableCategories.Contains("anime"));
        Assert.IsTrue(instant.RequestableCategories.Contains("anime"), "Instant is approval policy; the card still offers Request.");
    }

    [TestMethod]
    public async Task InstantOnlyChangesTheStateOfTheResultNeverTheAction()
    {
        await using var host = await RequestHost.CreateAsync();
        await host.Fixture.Capabilities.SetUserOverrideAsync(Alice, WorkMediaType.Movie, MediaCapability.Request);
        await host.Fixture.Capabilities.SetUserOverrideAsync("bob", WorkMediaType.Movie, MediaCapability.Instant);
        var executor = new RecordingExecutor(MediaAcquisitionKind.Movie);

        var waiting = ResultOf(await host.Page(Alice, executor).OnPostRequestAsync(Form("movie", Movie), CancellationToken.None));
        var approved = ResultOf(await host.Page("bob", executor).OnPostRequestAsync(Form("movie", "551"), CancellationToken.None));

        Assert.AreEqual("Waiting for approval", waiting.StateLabel);
        Assert.AreEqual(AcquisitionRequestStatus.Pending, waiting.Request.Status);
        Assert.AreEqual("Downloading", approved.StateLabel);
        Assert.AreEqual(1, executor.Runs, "Only the instant profile's request reached the executor.");
        Assert.IsFalse(waiting.Summary.Count > 0 || approved.Summary.Count > 0, "A movie has no scope to summarize.");
    }

    [TestMethod]
    public async Task AMovieOrSeriesRequestIsAboutTheCanonicalTmdbWorkNeverAProviderCandidate()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);

        var movie = ResultOf(await page.OnPostRequestAsync(Form("movie", "00550"), CancellationToken.None));
        var series = ResultOf(await page.OnPostRequestAsync(Form("tv", BreakingBad, scope: "all"), CancellationToken.None));

        Assert.AreEqual(Movie, movie.Request.ExternalId, "The provider id is normalized before it is stored.");
        foreach (var (type, externalId) in new[] { (WorkMediaType.Movie, Movie), (WorkMediaType.Series, BreakingBad) })
        {
            Assert.IsTrue(
                await host.Fixture.Db.WorkExternalIdentities.AnyAsync(identity => identity.Provider == "tmdb" && identity.MediaType == type && identity.ExternalId == externalId),
                $"{type} {externalId} must be a canonical Work before its request exists.");
        }

        Assert.AreEqual(BreakingBad, series.Request.ExternalId);
    }

    [TestMethod]
    [DataRow("not a language", null)]
    [DataRow(null, "%%%")]
    [DataRow("off", null)]
    public async Task AnAnimeRequestWithAnInvalidLanguageIsRejectedAndCreatesNothing(string? audio, string? subtitles)
    {
        await using var host = await RequestHost.CreateAsync();

        var result = await host.Page(Alice).OnPostRequestAsync(Form("anime", "154587", audio: audio, subtitles: subtitles), CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestResult>(result, "Subtitles may be switched off, audio may not.");
        Assert.AreEqual(0, (await host.Fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task TheStoreRefusesASecondOpenRequestForTheSameTitle()
    {
        await using var host = await RequestHost.CreateAsync();
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", Movie, "Fight Club", null, null);
        await host.Fixture.Store.CreateAsync(draft, Alice, AcquisitionRequestStatus.Pending, null, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<OpenRequestExistsException>(
            () => host.Fixture.Store.CreateAsync(draft, "bob", AcquisitionRequestStatus.Pending, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task TwoProfilesRequestingTheSameTitleAtOnceShareOneOpenRequestAndNeitherFails()
    {
        await using var host = await RequestHost.CreateAsync();
        await using var secondDb = host.Fixture.OpenContext();
        var first = host.Fixture.Service(Alice, AccountRole.User);
        var second = new AcquisitionRequestService(
            new AcquisitionAccessStore(secondDb),
            [],
            AcquisitionAccessFixture.Account("bob", AccountRole.User),
            new MediaCapabilityService(host.Fixture.Capabilities),
            host.Fixture.Settings,
            host.Fixture.Events,
            NullLogger<AcquisitionRequestService>.Instance);

        for (var title = 1; title <= 8; title++)
        {
            var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", title.ToString(), "A movie", null, null);

            var outcomes = await Task.WhenAll(first.SubmitWithOutcomeAsync(draft, CancellationToken.None), second.SubmitWithOutcomeAsync(draft, CancellationToken.None));

            Assert.AreEqual(outcomes[0].Request.Id, outcomes[1].Request.Id, "Both profiles end on the same open request.");
            Assert.AreEqual(1, outcomes.Count(outcome => !outcome.AlreadyRequested), "Exactly one of them created it; the other joined it.");
        }

        Assert.AreEqual(8, (await host.Fixture.Store.ListAsync(null, null, openOnly: true, 50, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task AProfileWithoutTheCapabilityIsRefusedByEveryRequestHandler()
    {
        await using var host = await RequestHost.CreateAsync();
        await host.Fixture.Capabilities.SetUserOverrideAsync(Alice, WorkMediaType.Series, MediaCapability.Browse);
        var page = host.Page(Alice);

        Assert.IsInstanceOfType<ForbidResult>(await page.OnPostResolveAsync("tv", "tmdb", BreakingBad, CancellationToken.None));
        Assert.IsInstanceOfType<ForbidResult>(await page.OnPostRequestAsync(Form("tv", BreakingBad, scope: "all"), CancellationToken.None));
        Assert.AreEqual(0, (await host.Fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count);
        await page.OnGetAsync(CancellationToken.None);
        Assert.IsFalse(page.RequestableCategories.Contains("tv"));
    }

    [TestMethod]
    public async Task ASeriesDialogOffersItsOwnSeasonsAndEpisodesAndAMovieOffersNoScope()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);

        var series = SettingsOf(await page.OnPostResolveAsync("tv", "tmdb", BreakingBad, CancellationToken.None));
        var movie = SettingsOf(await page.OnPostResolveAsync("movie", "tmdb", Movie, CancellationToken.None));

        Assert.IsTrue(series.OffersScope);
        CollectionAssert.AreEqual(new[] { 1, 2 }, series.Seasons.Select(season => season.Number).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 1 }, series.Seasons.Select(season => season.Episodes.Count).ToArray());
        Assert.IsTrue(series.Seasons.All(season => season.Id is not null));
        Assert.IsFalse(movie.OffersScope);
        Assert.IsFalse(movie.OffersLanguage);
        Assert.AreEqual(1, await host.Fixture.Db.Works.CountAsync(work => work.MediaType == WorkMediaType.Movie), "Resolving made the canonical Work, not a request.");
        Assert.AreEqual(0, (await host.Fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count);
    }

    [TestMethod]
    [DataRow("all", VideoRequestScope.AllCurrentAndFuture, "All current + future")]
    [DataRow("future", VideoRequestScope.FutureOnly, "Future only")]
    public async Task AnAutomaticSeriesScopeRequestsCurrentAndFutureOrFutureOnlyWithoutIds(string scope, VideoRequestScope expected, string summary)
    {
        await using var host = await RequestHost.CreateAsync();

        var result = ResultOf(await host.Page(Alice).OnPostRequestAsync(Form("tv", BreakingBad, scope: scope), CancellationToken.None));

        var payload = VideoRequestPayload.Parse(result.Request.PayloadJson)!;
        Assert.AreEqual(expected, payload.Scope);
        Assert.IsTrue(payload.MonitorFuture, "Future monitoring is part of the Scope, never a second toggle.");
        Assert.AreEqual(0, payload.SelectedEpisodeIds.Length);
        Assert.AreEqual(summary, result.Summary.Single());
    }

    [TestMethod]
    public async Task ACustomSeriesScopeStoresTheChosenSeasonsAndEpisodesOfItsOwnWork()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);
        var settings = SettingsOf(await page.OnPostResolveAsync("tv", "tmdb", BreakingBad, CancellationToken.None));
        var form = Form("tv", BreakingBad, scope: "custom");
        form.SeasonIds = [settings.Seasons[0].Id!.Value];
        form.EpisodeIds = [settings.Seasons[1].Episodes[0].Id];
        form.MonitorFuture = true;

        var result = ResultOf(await page.OnPostRequestAsync(form, CancellationToken.None));

        var payload = VideoRequestPayload.Parse(result.Request.PayloadJson)!;
        Assert.AreEqual(VideoRequestScope.Custom, payload.Scope);
        CollectionAssert.AreEqual(form.SeasonIds, payload.SelectedSeasonIds);
        CollectionAssert.AreEqual(form.EpisodeIds, payload.SelectedEpisodeIds);
        Assert.IsTrue(payload.MonitorFuture);
        Assert.AreEqual("Custom · Seasons: 1 · Episodes: 1 · Future releases", result.Summary.Single());
    }

    [TestMethod]
    public async Task ASeriesScopeNeverReachesIdsOfAnotherWork()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);
        var own = SettingsOf(await page.OnPostResolveAsync("tv", "tmdb", BreakingBad, CancellationToken.None));
        var foreign = SettingsOf(await page.OnPostResolveAsync("tv", "tmdb", OtherSeries, CancellationToken.None));

        var foreignSeason = Form("tv", BreakingBad, scope: "custom");
        foreignSeason.SeasonIds = [own.Seasons[0].Id!.Value, foreign.Seasons[0].Id!.Value];
        var foreignEpisode = Form("tv", BreakingBad, scope: "custom");
        foreignEpisode.EpisodeIds = [foreign.Seasons[0].Episodes[0].Id];
        var unknown = Form("tv", BreakingBad, scope: "custom");
        unknown.EpisodeIds = [Guid.NewGuid()];

        foreach (var form in new[] { foreignSeason, foreignEpisode, unknown })
        {
            Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostRequestAsync(form, CancellationToken.None));
        }

        Assert.AreEqual(0, (await host.Fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task ASeriesScopeNeedsAKnownNameAndOnlyCustomSelectsContent()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);
        var own = SettingsOf(await page.OnPostResolveAsync("tv", "tmdb", BreakingBad, CancellationToken.None));

        var noScope = Form("tv", BreakingBad);
        var unknownScope = Form("tv", BreakingBad, scope: "everything");
        var emptyCustom = Form("tv", BreakingBad, scope: "custom");
        var idsWithAll = Form("tv", BreakingBad, scope: "all");
        idsWithAll.SeasonIds = [own.Seasons[0].Id!.Value];
        var language = Form("tv", BreakingBad, scope: "all");
        language.Audio = "ja";

        foreach (var form in new[] { noScope, unknownScope, emptyCustom, idsWithAll, language })
        {
            Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostRequestAsync(form, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task AMovieAndTheOtherKindsRejectAScopeAndOnlyAnimeTakesLanguages()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);

        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostRequestAsync(Form("movie", Movie, scope: "all"), CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostRequestAsync(Form("movie", Movie, audio: "ja"), CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostRequestAsync(Form("anime", "154587", scope: "custom"), CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostRequestAsync(Form("anime", "154587", audio: "not a language"), CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostRequestAsync(Form("manga", "1", audio: "ja"), CancellationToken.None));
        Assert.AreEqual(0, (await host.Fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count);

        var anime = ResultOf(await page.OnPostRequestAsync(Form("anime", "154587", audio: "ja", subtitles: "de"), CancellationToken.None));
        Assert.AreEqual("ja", anime.Request.Options.AudioLanguage);
        Assert.AreEqual("de", anime.Request.Options.SubtitleLanguage);
        CollectionAssert.AreEqual(new[] { "All current + future", "Audio: 日本語", "Subtitles: Deutsch" }, anime.Summary.ToArray());
    }

    [TestMethod]
    public async Task ARequestedTitleIsNotRequestedTwiceAndTheDialogShowsTheExistingRequest()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);

        var first = ResultOf(await page.OnPostRequestAsync(Form("movie", Movie), CancellationToken.None));
        var second = ResultOf(await page.OnPostRequestAsync(Form("movie", Movie), CancellationToken.None));
        var settings = SettingsOf(await page.OnPostResolveAsync("movie", "tmdb", Movie, CancellationToken.None));

        Assert.IsFalse(first.AlreadyRequested);
        Assert.IsTrue(second.AlreadyRequested);
        Assert.AreEqual(first.Request.Id, second.Request.Id);
        Assert.AreEqual(first.Request.Id, settings.Existing?.Id);
        Assert.AreEqual(1, (await host.Fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task AnUnresolvableProviderIdentityIsRejectedBeforeAnyRequestExists()
    {
        await using var host = await RequestHost.CreateAsync();
        var page = host.Page(Alice);

        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostResolveAsync("movie", "anilist", Movie, CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostResolveAsync("movie", "tmdb", "not-a-number", CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostResolveAsync("anime", "tmdb", "154587", CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostResolveAsync("games", "igdb", "1", CancellationToken.None));
        var providerFailure = await page.OnPostResolveAsync("movie", "tmdb", "404", CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ((StatusCodeResult)providerFailure).StatusCode, "A provider failure is a retryable error, not a request.");
    }

    [TestMethod]
    public async Task TheDialogMarkupShowsOnlyTheGroupsOfTheMediaKindAndNeverAnAddAction()
    {
        await using var renderer = await DiscoverPartialRenderer.CreateAsync();
        var ui = UiTextBundle.English;
        var episodes = new[] { new VideoRequestEpisode(Guid.NewGuid(), 1, "Pilot", new DateTime(2008, 1, 20, 0, 0, 0, DateTimeKind.Utc)) };
        var seasons = new[] { new VideoRequestSeason(Guid.NewGuid(), 1, false, episodes), new VideoRequestSeason(Guid.NewGuid(), 0, true, []) };

        var series = WebUtility.HtmlDecode(await renderer.RenderAsync(
            "/Pages/Discover/_DiscoverRequestSettings.cshtml",
            new DiscoverRequestSettingsView(ui, MediaAcquisitionKind.Tv, null, seasons, null, null)));
        var movie = await renderer.RenderAsync(
            "/Pages/Discover/_DiscoverRequestSettings.cshtml",
            new DiscoverRequestSettingsView(ui, MediaAcquisitionKind.Movie, null, [], null, null));
        var anime = WebUtility.HtmlDecode(await renderer.RenderAsync(
            "/Pages/Discover/_DiscoverRequestSettings.cshtml",
            new DiscoverRequestSettingsView(ui, MediaAcquisitionKind.Anime, null, [], "ja", "off")));
        var dialog = await renderer.RenderAsync("/Pages/Discover/_DiscoverRequestDialog.cshtml", ui);

        StringAssert.Contains(series, "<option value=\"all\">All current + future</option>");
        StringAssert.Contains(series, "<option value=\"future\">Future only</option>");
        StringAssert.Contains(series, "<option value=\"custom\">Custom</option>");
        StringAssert.Contains(series, "Included content");
        StringAssert.Contains(series, "Season 1");
        StringAssert.Contains(series, "Specials");
        StringAssert.Contains(series, "Future seasons and episodes");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(series, "data-dc-rq-scope[ >]").Count, "One Scope control, no second independent one.");
        Assert.IsFalse(series.Contains("name=\"audio\"", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, movie.Trim());
        StringAssert.Contains(anime, "name=\"audio\"");
        StringAssert.Contains(anime, "<option value=\"ja\" selected=\"selected\">日本語</option>");
        Assert.IsFalse(anime.Contains("data-dc-rq-scope", StringComparison.Ordinal));
        StringAssert.Contains(dialog, "data-dc-rq-submit");
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(dialog, @">\s*Request\s*</button>"));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(dialog, @">\s*Add\s*</button>"));
    }

    [TestMethod]
    public async Task TheSuccessStateNamesTheRequestTheScopeAndTheApprovalState()
    {
        await using var renderer = await DiscoverPartialRenderer.CreateAsync();
        var request = new AcquisitionRequest(
            Guid.NewGuid(), MediaAcquisitionKind.Tv, "tmdb", BreakingBad, "Breaking Bad", null, null, null, Alice,
            AcquisitionRequestStatus.Pending, null, null, null, DateTime.UtcNow, DateTime.UtcNow, null, null);

        var created = WebUtility.HtmlDecode(await renderer.RenderAsync(
            "/Pages/Discover/_DiscoverRequestResult.cshtml",
            new DiscoverRequestResultView(UiTextBundle.English, request, false, ["Future only"], 0)));
        var existing = WebUtility.HtmlDecode(await renderer.RenderAsync(
            "/Pages/Discover/_DiscoverRequestResult.cshtml",
            new DiscoverRequestResultView(UiTextBundle.English, request, true, [], 0)));

        StringAssert.Contains(created, ">Request created</h3>");
        StringAssert.Contains(created, "<li>Future only</li>");
        StringAssert.Contains(created, ">Waiting for approval</strong>");
        StringAssert.Contains(created, "data-status=\"pending\"");
        StringAssert.Contains(created, "href=\"/Requests\"");
        StringAssert.Contains(existing, ">Already requested</h3>");
    }

    [TestMethod]
    public void NoConsumerSurfaceOffersAddAsAnAcquisitionAction()
    {
        var web = Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web");
        var sources = new[] { "Pages", "wwwroot/js", "Features/Localization" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(web, folder), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cshtml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllText);
        var forbidden = new[]
        {
            "discover.card.add\"",
            "books.add.add\"",
            "requests.new.submitAdd",
            "requests.new.added",
            "AddCreatesRequest",
            "data-dc-add>",
            "data-dc-card-add",
            "novels.index.addNovel"
        };

        var offenders = sources
            .SelectMany(source => forbidden.Where(token => source.Value.Contains(token, StringComparison.Ordinal)).Select(token => $"{Path.GetFileName(source.Key)}: {token}"))
            .ToArray();

        Assert.AreEqual(0, offenders.Length, string.Join(Environment.NewLine, offenders));
    }

    private static DiscoverRequestForm Form(string category, string externalId, string? scope = null, string? audio = null, string? subtitles = null) =>
        new()
        {
            Category = category,
            Provider = category is "anime" ? "anilist" : category is "manga" ? "anilist" : "tmdb",
            ExternalId = externalId,
            Title = "A title",
            Scope = scope,
            Audio = audio,
            Subtitles = subtitles
        };

    private static DiscoverRequestResultView ResultOf(IActionResult result) =>
        (DiscoverRequestResultView)((PartialViewResult)result).ViewData.Model!;

    private static DiscoverRequestSettingsView SettingsOf(IActionResult result) =>
        (DiscoverRequestSettingsView)((PartialViewResult)result).ViewData.Model!;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Jularr repository root.");
    }

    /// <summary>The Discover page model on a real database with a stubbed TMDB, as the Request handlers see it.</summary>
    private sealed class RequestHost : IAsyncDisposable
    {
        private readonly HttpClient client;

        private RequestHost(AcquisitionAccessFixture fixture, HttpClient client)
        {
            Fixture = fixture;
            this.client = client;
        }

        public AcquisitionAccessFixture Fixture { get; }

        public static async Task<RequestHost> CreateAsync() =>
            new(await AcquisitionAccessFixture.CreateAsync(), new HttpClient(new StubHandler(Tmdb)) { BaseAddress = new Uri("https://api.themoviedb.org/3/") });

        public DiscoverIndexModel Page(string profileId, params IAcquisitionRequestExecutor[] executors)
        {
            var clock = TimeProvider.System;
            var works = new WorkService(Fixture.Db);
            var structure = new WorkStructureService(Fixture.Db);
            var tmdb = new TmdbDiscoveryProvider(
                client,
                new ConfigurationManager { ["Providers:Tmdb:ApiKey"] = "test-key" },
                new ProviderExecutor(new ProviderRateLimiter(), new ProviderHealthTracker(clock), clock, NullLogger<ProviderExecutor>.Instance),
                new ProviderResponseCache(clock),
                works,
                structure,
                Fixture.Db,
                new LegacyWorkBridge(Fixture.Db, works, structure));
            var page = new DiscoverIndexModel(
                null!,
                null!,
                tmdb,
                Fixture.Db,
                null!,
                null!,
                AcquisitionAccessFixture.Account(profileId, AccountRole.User),
                null!,
                Fixture.Service(profileId, AccountRole.User, executors),
                Fixture.Store,
                new VideoRequestScopeResolver(Fixture.Db),
                null!,
                null!,
                null!,
                NullLogger<DiscoverIndexModel>.Instance);
            var httpContext = new DefaultHttpContext
            {
                User = AcquisitionAccessFixture.Principal(profileId, AccountRole.User),
                RequestServices = new ServiceCollection().AddSingleton<IModelMetadataProvider, EmptyModelMetadataProvider>().BuildServiceProvider()
            };
            page.PageContext = new PageContext
            {
                HttpContext = httpContext,
                ViewData = new ViewDataDictionary<DiscoverIndexModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            };
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await Fixture.DisposeAsync();
        }

        private static HttpResponseMessage Tmdb(HttpRequestMessage request) =>
            request.RequestUri!.AbsolutePath switch
            {
                "/3/movie/550" => Json("""{ "title": "Fight Club", "original_title": "Fight Club", "original_language": "en", "release_date": "1999-10-15" }"""),
                "/3/movie/551" => Json("""{ "title": "Se7en", "original_title": "Se7en", "original_language": "en", "release_date": "1995-09-22" }"""),
                "/3/tv/1396" => Json(Series("Breaking Bad", 2)),
                "/3/tv/1399" => Json(Series("Game of Thrones", 1)),
                "/3/tv/1396/season/1" => Json(Episodes(2)),
                "/3/tv/1396/season/2" => Json(Episodes(1)),
                "/3/tv/1399/season/1" => Json(Episodes(1)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

        private static string Series(string name, int seasons) =>
            JsonSerializer.Serialize(new
            {
                name,
                original_name = name,
                original_language = "en",
                first_air_date = "2008-01-20",
                seasons = Enumerable.Range(1, seasons).Select(number => new { season_number = number, name = $"Season {number}" })
            });

        private static string Episodes(int count) =>
            JsonSerializer.Serialize(new
            {
                episodes = Enumerable.Range(1, count).Select(number => new { episode_number = number, name = $"Episode {number}", air_date = "2008-01-20" })
            });

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
