using System.Net;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// Movie and Series Discover on the real coordinator and section composition: a TMDB that is not configured, switched off, refusing its credential or
/// failing is never an empty result, a genuine empty answer is, and the owner and everybody else get different words and actions (#872). With a TMDB that
/// answers, Series flow through the same path as Movies (#871).
/// </summary>
[TestClass]
public sealed class TmdbDiscoverStatesTests
{
    private const string BodyView = "/Pages/Shared/_DiscoverBody.cshtml";

    private static readonly UiTextBundle Ui = UiTextBundle.English;

    private static readonly IReadOnlySet<WorkMediaType> EveryType = new HashSet<WorkMediaType>(WorkMediaTypes.All);

    /// <summary>A trending/tv/day answer as TMDB sends it: extra fields, a title without a first air date and one without a poster.</summary>
    private const string TvList = """
        {
          "page": 1,
          "results": [
            { "adult": false, "backdrop_path": "/b1.jpg", "id": 95396, "name": "Severance", "original_name": "Severance", "overview": "Mark leads a team.", "poster_path": "/p1.jpg",
              "media_type": "tv", "original_language": "en", "genre_ids": [18, 9648], "popularity": 120.5, "first_air_date": "2022-02-17", "vote_average": 8.4, "vote_count": 1200, "origin_country": ["US"] },
            { "adult": false, "backdrop_path": null, "id": 1396, "name": "Breaking Bad", "original_name": "Breaking Bad", "overview": "", "poster_path": "/p2.jpg",
              "media_type": "tv", "original_language": "en", "genre_ids": [18], "popularity": 99.1, "first_air_date": "2008-01-20", "vote_average": 8.9, "vote_count": 12000, "origin_country": ["US"] },
            { "adult": false, "backdrop_path": null, "id": 777001, "name": "Untitled Announced Series", "original_name": "Untitled Announced Series", "overview": "", "poster_path": null,
              "media_type": "tv", "original_language": "en", "genre_ids": [], "popularity": 3.0, "first_air_date": "", "vote_average": 0, "vote_count": 0, "origin_country": [] },
            { "adult": false, "backdrop_path": null, "id": 95396, "name": "Severance", "original_name": "Severance", "overview": "Duplicate id in the same page.", "poster_path": "/p1.jpg",
              "media_type": "tv", "original_language": "en", "genre_ids": [18], "popularity": 1.0, "first_air_date": "2022-02-17", "vote_average": 8.4, "vote_count": 1200, "origin_country": ["US"] }
          ],
          "total_pages": 1,
          "total_results": 4
        }
        """;

    private const string MovieList = """
        { "page": 1, "results": [ { "id": 550, "title": "Fight Club", "original_title": "Fight Club", "overview": "x", "poster_path": "/m1.jpg", "release_date": "1999-10-15", "genre_ids": [18], "vote_average": 8.4 },
                                  { "id": 680, "title": "Pulp Fiction", "original_title": "Pulp Fiction", "overview": "y", "poster_path": "/m2.jpg", "release_date": "1994-09-10", "genre_ids": [80], "vote_average": 8.5 } ] }
        """;

    private static DiscoveryAudience Audience(bool owner, params WorkMediaType[] visible) => new("profile", owner, visible.Length == 0 ? EveryType : new HashSet<WorkMediaType>(visible));

    private static DiscoverContext Context(bool owner) =>
        new(
            Ui,
            LibraryLanguagePreference.None,
            new Dictionary<(Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind Kind, string ExternalId), Jularr.Web.Features.Acquisition.Access.AcquisitionRequest>(),
            new Dictionary<string, DiscoverLocalFacts>(),
            new Dictionary<string, Guid?>(),
            new HashSet<string>(["movie", "tv"], StringComparer.Ordinal),
            owner);

    private sealed record Rig(DiscoveryCoordinator Coordinator, Jularr.Web.Data.AppDbContext Db, List<string> Requests) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Rig> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage> tmdb, TmdbCredentialStore? credentials = null, InstanceModuleSettings? modules = null)
    {
        var db = await MediaCoreTestSupport.CreateDbAsync();
        var requests = new List<string>();
        var store = credentials ?? TmdbTestSupport.Credentials();
        var provider = TmdbDiscoveryTests.Provider(db, TmdbDiscoveryTests.Client(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return tmdb(request);
        }), store);
        var instance = modules is null ? null : new FixedModules(modules);
        var coordinator = new DiscoveryCoordinator(store, null!, db, DiscoveryTestSupport.Flights(providers: [provider]), TimeProvider.System, NullLogger<DiscoveryCoordinator>.Instance, instance);
        return new Rig(coordinator, db, requests);
    }

    private sealed class FixedModules(InstanceModuleSettings settings) : IInstanceModuleService
    {
        public Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task<bool> IsEnabledAsync(InstanceModule module, CancellationToken cancellationToken = default) => Task.FromResult(settings.IsEnabled(module));

        public Task<InstanceModuleSettings> SetAsync(InstanceModule module, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstanceModuleSettings> SaveAsync(InstanceModuleSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Answer(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("/movie", StringComparison.Ordinal) ? TmdbDiscoveryTests.Json(MovieList) : TmdbDiscoveryTests.Json(TvList);

    private static TmdbCredentialStore Stored() =>
        TmdbTestSupport.Credentials(apiKey: null, directory: Path.Combine(Path.GetTempPath(), "jularr-tmdb-" + Guid.NewGuid().ToString("N")));

    private static async Task<DiscoveryBatch> LoadAsync(Rig rig, DiscoveryCategory category, DiscoveryMode mode, bool owner, string query = "", params WorkMediaType[] visible)
    {
        var load = await rig.Coordinator.LoadAsync([new DiscoveryRequest(query, category, mode)], Audience(owner, visible), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);
        return load.Batches[0];
    }

    private static (IReadOnlyList<DiscoverSectionView> Sections, int Total) Compose(DiscoveryBatch batch, DiscoveryCategory category, bool owner, string text = "") =>
        DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Category = category, Text = text }, Context(owner));

    [TestMethod]
    [DataRow(DiscoveryMode.Trending, "/3/trending/tv/day")]
    [DataRow(DiscoveryMode.Top, "/3/tv/popular")]
    [DataRow(DiscoveryMode.New, "/3/discover/tv")]
    [DataRow(DiscoveryMode.Upcoming, "/3/discover/tv")]
    [DataRow(DiscoveryMode.Search, "/3/search/tv")]
    public async Task SeriesFlowFromTheSourceThroughTheRealSectionCompositionForEveryMode(DiscoveryMode mode, string path)
    {
        await using var rig = await CreateAsync(Answer);

        var batch = await LoadAsync(rig, DiscoveryCategory.Series, mode, owner: false, query: mode == DiscoveryMode.Search ? "Severance" : "");
        var (sections, total) = Compose(batch, DiscoveryCategory.Series, owner: false, text: mode == DiscoveryMode.Search ? "Severance" : "");

        Assert.AreEqual(path, new Uri("https://x" + rig.Requests.Single()).AbsolutePath, "The source calls the TMDB TV endpoint of the mode.");
        Assert.AreEqual(DiscoverySourceState.Ready, batch.Sources.Single().State);
        Assert.AreEqual(4, batch.Items.Count, "Four raw rows are mapped, even those without a poster or a first air date.");
        Assert.AreEqual(3, total, "The same TMDB id twice in one answer is one title.");
        var section = sections.Single();
        Assert.AreEqual(DiscoverySectionState.Ready, section.State);
        Assert.AreEqual(3, section.Cards.Count, "No filter is active, so nothing but the duplicate is removed.");
        CollectionAssert.AreEqual(new[] { "Severance", "Breaking Bad", "Untitled Announced Series" }, section.Cards.Select(card => card.Title).ToArray());
        Assert.IsTrue(section.Cards.All(card => card.Category == "tv"));
        Assert.IsNull(section.Cards[2].Year);
    }

    [TestMethod]
    public async Task TheSeriesYearFilterNarrowsByYearOnlyAndATitleWithoutOneIsNotAHiddenCauseOfAnEmptyBoard()
    {
        await using var rig = await CreateAsync(Answer);
        var batch = await LoadAsync(rig, DiscoveryCategory.Series, DiscoveryMode.Trending, owner: false);

        var (narrowed, total) = DiscoverSectionComposer.Results(batch, new DiscoverBrowseQuery { Category = DiscoveryCategory.Series, YearFrom = 2008, YearTo = 2008 }, Context(false));

        Assert.AreEqual(3, total);
        Assert.AreEqual("Breaking Bad", narrowed.Single().Cards.Single().Title);
        var (withoutFilter, _) = Compose(batch, DiscoveryCategory.Series, owner: false);
        Assert.AreEqual(3, withoutFilter.Single().Cards.Count);
    }

    [TestMethod]
    public async Task MoviesAndSeriesOnTheSameConfigurationBothWorkAndTheLandingHasARowForEach()
    {
        await using var rig = await CreateAsync(Answer);

        var load = await rig.Coordinator.LoadAsync(
            [new DiscoveryRequest("", DiscoveryCategory.Movie, DiscoveryMode.Trending), new DiscoveryRequest("", DiscoveryCategory.Series, DiscoveryMode.Trending)],
            Audience(false), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.AreEqual(2, load.Batches[0].Items.Count);
        Assert.AreEqual(4, load.Batches[1].Items.Count);
        var rows = new[]
        {
            new DiscoverLandingRow("trending-movie", "Trending", null, load.Batches[0].Items, load.Batches[0].Sources, "Movie"),
            new DiscoverLandingRow("trending-series", "Trending", null, load.Batches[1].Items, load.Batches[1].Sources, "Series")
        };
        CollectionAssert.AreEqual(new[] { DiscoverySectionState.Ready, DiscoverySectionState.Ready }, DiscoverSectionComposer.Landing(rows, Context(false)).Select(section => section.State).ToArray());
    }

    [TestMethod]
    public async Task WithoutACredentialNothingIsAskedAndTheSectionSaysSoInsteadOfBeingEmpty()
    {
        await using var rig = await CreateAsync(Answer, Stored());

        var batch = await LoadAsync(rig, DiscoveryCategory.Series, DiscoveryMode.Trending, owner: true);

        Assert.AreEqual(0, rig.Requests.Count);
        Assert.AreEqual(DiscoverySourceState.NotConfigured, batch.Sources.Single().State);
        var admin = Compose(batch, DiscoveryCategory.Series, owner: true).Sections.Single();
        Assert.AreEqual(DiscoverySectionState.NotConfigured, admin.State);
        Assert.AreEqual("TMDB is not configured", admin.Notice!.Title);
        Assert.AreEqual("Movie and Series discovery needs a TMDB provider connection.", admin.Notice.Body);
        Assert.AreEqual("Configure TMDB", admin.Notice.ActionLabel);
        Assert.AreEqual("/Admin/Providers", admin.Notice.ActionUrl);
        var user = Compose(batch, DiscoveryCategory.Series, owner: false).Sections.Single();
        Assert.AreEqual("Movie & Series discovery is currently unavailable", user.Notice!.Title);
        Assert.AreEqual("An administrator needs to finish the media provider setup.", user.Notice.Body);
        Assert.IsNull(user.Notice.ActionUrl, "A viewer who may not configure providers gets no admin link.");
        Assert.IsNull(user.Notice.ActionLabel);
    }

    [TestMethod]
    public async Task ASwitchedOffTmdbIsDistinctFromANotConfiguredOneForTheOwnerAndTheSameSetupMessageForOthers()
    {
        var store = Stored();
        await store.SaveAsync(new TmdbSettingsUpdate(false, "a-token", null), CancellationToken.None);
        await using var rig = await CreateAsync(Answer, store);

        var batch = await LoadAsync(rig, DiscoveryCategory.Movie, DiscoveryMode.Top, owner: true);

        Assert.AreEqual(DiscoverySourceState.Disabled, batch.Sources.Single().State);
        Assert.AreEqual(0, rig.Requests.Count);
        Assert.AreEqual("TMDB is turned off", Compose(batch, DiscoveryCategory.Movie, owner: true).Sections.Single().Notice!.Title);
        Assert.AreEqual("Movie & Series discovery is currently unavailable", Compose(batch, DiscoveryCategory.Movie, owner: false).Sections.Single().Notice!.Title);
    }

    [TestMethod]
    public async Task ARefusedCredentialIsAnAuthenticationFailureNotAnEmptyResultAndUsersOnlySeeATemporaryProblem()
    {
        await using var rig = await CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var batch = await LoadAsync(rig, DiscoveryCategory.Series, DiscoveryMode.Trending, owner: true);

        Assert.AreEqual(DiscoverySourceState.AuthFailed, batch.Sources.Single().State);
        var admin = Compose(batch, DiscoveryCategory.Series, owner: true).Sections.Single();
        Assert.AreEqual("TMDB authentication failed", admin.Notice!.Title);
        Assert.AreEqual("Check the saved TMDB credential and test the connection.", admin.Notice.Body);
        Assert.AreEqual("/Admin/Providers", admin.Notice.ActionUrl);
        var user = Compose(batch, DiscoveryCategory.Series, owner: false).Sections.Single();
        Assert.AreEqual("Movie & Series discovery is temporarily unavailable", user.Notice!.Title);
        Assert.IsNull(user.Notice.ActionUrl);
    }

    [TestMethod]
    public async Task ATemporaryFailureIsDistinctFromAnEmptyAnswerAndAGenuineEmptyAnswerStaysEmpty()
    {
        await using var down = await CreateAsync(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var failed = await LoadAsync(down, DiscoveryCategory.Series, DiscoveryMode.Search, owner: true, query: "anything");
        Assert.AreEqual(DiscoverySourceState.Unavailable, failed.Sources.Single().State);
        var notice = Compose(failed, DiscoveryCategory.Series, owner: true, "anything").Sections.Single().Notice!;
        Assert.AreEqual("TMDB is temporarily unavailable", notice.Title);
        Assert.AreEqual("Try again shortly.", notice.Body);
        Assert.IsNull(notice.ActionUrl);

        await using var empty = await CreateAsync(_ => TmdbDiscoveryTests.Json("{\"page\":1,\"results\":[]}"));
        var none = await LoadAsync(empty, DiscoveryCategory.Series, DiscoveryMode.Search, owner: true, query: "zzzz");
        Assert.AreEqual(DiscoverySourceState.Ready, none.Sources.Single().State);
        var (sections, total) = Compose(none, DiscoveryCategory.Series, owner: true, "zzzz");
        Assert.AreEqual(0, sections.Count, "A genuine empty answer has no section and no provider notice: the body says no results.");
        Assert.AreEqual(0, total);
    }

    [TestMethod]
    public async Task ADisabledModuleOrAHiddenMediaTypeShowsNothingAtAllNotEvenANotice()
    {
        await using var noTv = await CreateAsync(Answer, Stored(), InstanceModuleSettings.Default.With(InstanceModule.Tv, false));
        var moduleOff = await LoadAsync(noTv, DiscoveryCategory.Series, DiscoveryMode.Trending, owner: true);
        Assert.AreEqual(0, moduleOff.Sources.Count);

        await using var hidden = await CreateAsync(Answer, Stored());
        var notPermitted = await LoadAsync(hidden, DiscoveryCategory.Series, DiscoveryMode.Trending, owner: false, visible: [WorkMediaType.Anime, WorkMediaType.Movie]);
        Assert.AreEqual(0, notPermitted.Sources.Count);
        Assert.AreEqual(0, Compose(notPermitted, DiscoveryCategory.Series, owner: false).Sections.Count);
    }

    [TestMethod]
    public async Task OneNoticeCoversAllMovieAndSeriesRowsOfTheLandingForTheSameCause()
    {
        await using var rig = await CreateAsync(Answer, Stored());
        var requests = new[]
        {
            new DiscoveryRequest("", DiscoveryCategory.Movie, DiscoveryMode.Trending),
            new DiscoveryRequest("", DiscoveryCategory.Series, DiscoveryMode.Trending),
            new DiscoveryRequest("", DiscoveryCategory.Series, DiscoveryMode.Top)
        };

        var load = await rig.Coordinator.LoadAsync(requests, Audience(true), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);
        var rows = load.Batches.Select((batch, index) => new DiscoverLandingRow($"row-{index}", "Row", null, batch.Items, batch.Sources, index == 0 ? "Movie" : "Series")).ToArray();
        var sections = DiscoverSectionComposer.Landing(rows, Context(true));

        Assert.AreEqual(1, sections.Count);
        Assert.AreEqual("notice-tmdb-notconfigured", sections[0].Id);
    }

    [TestMethod]
    public async Task TheRenderedBodyNeverContainsASecretAKeyNameOrAnAdminLinkForAnOrdinaryViewer()
    {
        await using var rig = await CreateAsync(Answer, Stored());
        var batch = await LoadAsync(rig, DiscoveryCategory.Series, DiscoveryMode.Trending, owner: false);
        var (sections, total) = Compose(batch, DiscoveryCategory.Series, owner: false);
        var body = new DiscoverBodyView(Ui, new DiscoverBrowseQuery { Category = DiscoveryCategory.Series }, DiscoverBodyState.Sections, sections, total, 1, 0);
        await using var renderer = await DiscoverPartialRenderer.CreateAsync();

        var user = WebUtility.HtmlDecode(await renderer.RenderAsync(BodyView, body));

        StringAssert.Contains(user, "Movie &amp; Series discovery is currently unavailable".Replace("&amp;", "&"));
        Assert.IsFalse(user.Contains("/Admin", StringComparison.Ordinal));
        Assert.IsFalse(user.Contains("Providers:", StringComparison.Ordinal));
        Assert.IsFalse(user.Contains("Configure TMDB", StringComparison.Ordinal));
        var (adminSections, adminTotal) = Compose(batch, DiscoveryCategory.Series, owner: true);
        var admin = WebUtility.HtmlDecode(await renderer.RenderAsync(BodyView, body with { Sections = adminSections, Total = adminTotal }));
        StringAssert.Contains(admin, "href=\"/Admin/Providers\"");
        Assert.IsFalse(admin.Contains("Providers:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ARecordedTraceOfTheSeriesAndMoviePathsOnOneConfiguration()
    {
        var trace = new List<string>();
        await using var rig = await CreateAsync(Answer);
        (DiscoveryCategory, DiscoveryMode)[] paths =
        [
            (DiscoveryCategory.Series, DiscoveryMode.Trending),
            (DiscoveryCategory.Series, DiscoveryMode.Top),
            (DiscoveryCategory.Series, DiscoveryMode.New),
            (DiscoveryCategory.Series, DiscoveryMode.Upcoming),
            (DiscoveryCategory.Movie, DiscoveryMode.Trending)
        ];
        foreach (var (category, modes) in paths)
        {
            var batch = await LoadAsync(rig, category, modes, owner: false);
            var (sections, total) = Compose(batch, category, owner: false);
            var source = batch.Sources.Single();
            var shown = sections.SingleOrDefault();
            trace.Add($"{category}/{modes}: source={source.Source}:{source.State} mapped={batch.Items.Count} afterCollapse={total} afterFilter={shown?.Cards.Count ?? 0} section={shown?.State}");
        }

        Console.WriteLine(string.Join(Environment.NewLine, trace));
        Assert.IsTrue(trace.Take(4).All(line => line.Contains("mapped=4 afterCollapse=3 afterFilter=3 section=Ready", StringComparison.Ordinal)), string.Join("; ", trace));
        Assert.IsTrue(trace[4].Contains("mapped=2 afterCollapse=2 afterFilter=2 section=Ready", StringComparison.Ordinal), trace[4]);
    }
}
