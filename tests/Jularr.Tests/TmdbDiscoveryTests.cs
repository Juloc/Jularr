using System.Net;
using System.Text;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Providers;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class TmdbDiscoveryTests
{
    [TestMethod]
    public async Task SearchIsBoundedCachedAndDoesNotCreateDurableWorks()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            Assert.AreEqual("/3/search/movie", request.RequestUri!.AbsolutePath);
            return Json("""
                {
                  "results": [
                    {
                      "id": 550,
                      "title": "Fight Club",
                      "original_title": "Fight Club",
                      "overview": "An insomniac meets a soap maker.",
                      "poster_path": "/poster.jpg",
                      "backdrop_path": "/backdrop.jpg",
                      "release_date": "1999-10-15",
                      "genre_ids": [18],
                      "vote_average": 8.4
                    }
                  ]
                }
                """);
        });
        var provider = Provider(db, client);

        var request = new DiscoveryRequest("Fight Club", DiscoveryCategory.Movie, DiscoveryMode.Search, "Drama");
        var first = (await provider.DiscoverPageAsync(TmdbDiscoveryMediaType.Movie, request, 10, CancellationToken.None)).Items;
        var second = (await provider.DiscoverPageAsync(TmdbDiscoveryMediaType.Movie, request, 10, CancellationToken.None)).Items;

        Assert.AreEqual(1, calls, "The second identical provider request must hit ProviderResponseCache.");
        Assert.AreEqual(1, first.Count);
        Assert.AreEqual("550", first[0].ExternalId);
        Assert.AreEqual("movie", first[0].Category);
        Assert.AreEqual("https://image.tmdb.org/t/p/w780/backdrop.jpg", first[0].BackdropUrl, "The Preview hero comes from the list answer, with no further call.");
        Assert.AreEqual(first[0], second[0]);
        Assert.AreEqual(0, await db.Works.CountAsync(), "Browsing a provider feed must not become a second durable catalog.");
    }

    [TestMethod]
    public async Task MovieMaterializationReusesLegacyTmdbIdentityAndIsIdempotent()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var legacy = new Movie
        {
            Key = "fight-club-1999",
            Title = "Fight Club",
            Year = 1999,
            TmdbId = "550"
        };
        db.Movies.Add(legacy);
        await db.SaveChangesAsync();

        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            Assert.AreEqual("/3/movie/550", request.RequestUri!.AbsolutePath);
            return Json("""
                {
                  "title": "Fight Club",
                  "original_title": "Fight Club",
                  "original_language": "en",
                  "overview": "An insomniac meets a soap maker.",
                  "poster_path": "/poster.jpg",
                  "release_date": "1999-10-15",
                  "imdb_id": "tt0137523"
                }
                """);
        });
        var provider = Provider(db, client);

        Assert.IsTrue(TmdbDiscoveryProvider.TryNormalizeExternalId("00550", out var normalizedId));
        Assert.AreEqual("550", normalizedId);

        var first = await provider.EnsureCanonicalWorkAsync(
            TmdbDiscoveryMediaType.Movie, "00550", CancellationToken.None);
        var second = await provider.EnsureCanonicalWorkAsync(
            TmdbDiscoveryMediaType.Movie, "550", CancellationToken.None);

        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(1, await db.Works.CountAsync());
        Assert.AreEqual(1, calls, "TMDB details must be reused from the bounded provider cache.");
        Assert.IsTrue(await db.WorkExternalIdentities.AnyAsync(x =>
            x.WorkId == first.Id
            && x.MediaType == WorkMediaType.Movie
            && x.Provider == ProviderKeys.Tmdb
            && x.ExternalId == "550"));
        Assert.IsTrue(await db.WorkExternalIdentities.AnyAsync(x =>
            x.WorkId == first.Id
            && x.Provider == ProviderKeys.Imdb
            && x.ExternalId == "tt0137523"));
        Assert.IsTrue(await db.WorkSourceLinks.AnyAsync(x =>
            x.WorkId == first.Id
            && x.SourceKind == WorkSourceKind.Movie
            && x.SourceId == legacy.Id));
        Assert.IsTrue(await db.WorkFieldProvenance.AnyAsync(x =>
            x.WorkId == first.Id && x.FieldKey == "title" && x.Source == ProviderKeys.Tmdb));
    }

    [TestMethod]
    public async Task SeriesMaterializationCreatesCanonicalSeasonEpisodeStructure()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var calls = new List<string>();
        using var client = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            return path switch
            {
                "/3/tv/1396" => Json("""
                    {
                      "name": "Breaking Bad",
                      "original_name": "Breaking Bad",
                      "original_language": "en",
                      "overview": "A chemistry teacher turns to crime.",
                      "poster_path": "/bb.jpg",
                      "first_air_date": "2008-01-20",
                      "external_ids": {
                        "imdb_id": "tt0903747",
                        "tvdb_id": 81189
                      },
                      "seasons": [
                        { "season_number": 1, "name": "Season 1" }
                      ]
                    }
                    """),
                "/3/tv/1396/season/1" => Json("""
                    {
                      "episodes": [
                        { "episode_number": 1, "name": "Pilot", "air_date": "2008-01-20" },
                        { "episode_number": 2, "name": "Cat's in the Bag...", "air_date": "2008-01-27" }
                      ]
                    }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = Provider(db, client);

        var work = await provider.EnsureCanonicalWorkAsync(
            TmdbDiscoveryMediaType.Series, "1396", CancellationToken.None);

        Assert.AreEqual(WorkMediaType.Series, work.MediaType);
        Assert.AreEqual(1, await db.WorkSeasons.CountAsync(x => x.WorkId == work.Id));
        Assert.AreEqual(2, await db.WorkEpisodes.CountAsync(x => x.WorkId == work.Id));
        Assert.IsTrue(await db.WorkExternalIdentities.AnyAsync(x =>
            x.WorkId == work.Id && x.Provider == ProviderKeys.Tmdb && x.ExternalId == "1396"));
        Assert.IsTrue(await db.WorkExternalIdentities.AnyAsync(x =>
            x.WorkId == work.Id && x.Provider == ProviderKeys.Tvdb && x.ExternalId == "81189"));
        Assert.IsTrue(await db.WorkExternalIdentities.AnyAsync(x =>
            x.WorkId == work.Id && x.Provider == ProviderKeys.Imdb && x.ExternalId == "tt0903747"));
        CollectionAssert.AreEquivalent(
            new[] { "/3/tv/1396", "/3/tv/1396/season/1" },
            calls.ToArray());
    }

    private static async Task<(string Path, Dictionary<string, string> Query, bool HasMore)> AskAsync(DiscoveryRequest request, TmdbDiscoveryMediaType mediaType)
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        string path = "";
        var query = new Dictionary<string, string>();
        using var client = Client(http =>
        {
            path = http.RequestUri!.AbsolutePath;
            foreach (var pair in http.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                query[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts.ElementAtOrDefault(1) ?? "");
            }

            return Json("{\"total_pages\":3,\"results\":[]}");
        });
        var page = await Provider(db, client).DiscoverPageAsync(mediaType, request, 20, CancellationToken.None);
        return (path, query, page.HasMore);
    }

    [TestMethod]
    public async Task EveryBrowseViewAsksTmdbForItsOwnRankingAndNarrowedViewsUseTheDiscoverEndpoint()
    {
        var trending = await AskAsync(new DiscoveryRequest("", DiscoveryCategory.Movie, DiscoveryMode.Trending), TmdbDiscoveryMediaType.Movie);
        Assert.AreEqual("/3/trending/movie/day", trending.Path);
        Assert.IsFalse(trending.Query.ContainsKey("region"), "No region is ever sent: the application does not infer one.");

        var popular = await AskAsync(new DiscoveryRequest("", DiscoveryCategory.Movie, DiscoveryMode.Popular), TmdbDiscoveryMediaType.Movie);
        Assert.AreEqual("/3/discover/movie", popular.Path);
        Assert.AreEqual("vote_count.desc", popular.Query["sort_by"], "All-time popular is by votes, not by what is popular this week.");

        var rated = await AskAsync(new DiscoveryRequest("", DiscoveryCategory.Series, DiscoveryMode.TopRated), TmdbDiscoveryMediaType.Series);
        Assert.AreEqual("/3/tv/top_rated", rated.Path);

        var top = await AskAsync(new DiscoveryRequest("", DiscoveryCategory.Series, DiscoveryMode.Top), TmdbDiscoveryMediaType.Series);
        Assert.AreEqual("/3/tv/popular", top.Path);
    }

    [TestMethod]
    public async Task TheViewersFiltersAreSentToTmdbAndTheSecondPageIsAskedForByNumber()
    {
        var filter = new DiscoveryFilter(["Drama"], 2010, 2015, [MediaReleaseStatus.Finished]);
        var series = await AskAsync(new DiscoveryRequest("", DiscoveryCategory.Series, DiscoveryMode.TopRated) { Filter = filter, Page = 2 }, TmdbDiscoveryMediaType.Series);

        Assert.AreEqual("/3/discover/tv", series.Path);
        Assert.AreEqual("2", series.Query["page"]);
        Assert.AreEqual("18", series.Query["with_genres"]);
        Assert.AreEqual("3", series.Query["with_status"], "Finished is the ended status of TMDB.");
        Assert.AreEqual("2010-01-01", series.Query["first_air_date.gte"]);
        Assert.AreEqual("2015-12-31", series.Query["first_air_date.lte"]);
        Assert.AreEqual("vote_average.desc", series.Query["sort_by"]);
        Assert.AreEqual("500", series.Query["vote_count.gte"]);
        Assert.IsTrue(series.HasMore, "Page 2 of 3 is followed by another.");

        var last = await AskAsync(new DiscoveryRequest("", DiscoveryCategory.Movie, DiscoveryMode.Popular) { Page = 3 }, TmdbDiscoveryMediaType.Movie);
        Assert.IsFalse(last.HasMore, "The last page ends the view.");

        var upcoming = await AskAsync(new DiscoveryRequest("", DiscoveryCategory.Movie, DiscoveryMode.Upcoming) { Filter = new DiscoveryFilter(["Drama"]) }, TmdbDiscoveryMediaType.Movie);
        Assert.IsTrue(string.CompareOrdinal(upcoming.Query["primary_release_date.gte"], DateTime.UtcNow.ToString("yyyy-MM-dd")) > 0, "Upcoming only holds titles after today.");
    }

    internal static TmdbDiscoveryProvider Provider(
        Jularr.Web.Data.AppDbContext db,
        HttpClient client,
        TmdbCredentialStore? credentials = null,
        ProviderHealthTracker? health = null)
    {
        var clock = TimeProvider.System;
        health ??= new ProviderHealthTracker(clock);
        var works = new WorkService(db);
        var structure = new WorkStructureService(db);
        return new TmdbDiscoveryProvider(
            client,
            credentials ?? TmdbTestSupport.Credentials(),
            new ProviderExecutor(
                new ProviderRateLimiter(),
                health,
                clock,
                NullLogger<ProviderExecutor>.Instance),
            health,
            new ProviderResponseCache(clock),
            works,
            structure,
            db,
            new LegacyWorkBridge(db, works, structure),
            new WorkMetadataRefreshQueue(new WorkMetadataStore(db), new WorkMetadataRefreshSignal(), clock));
    }

    internal static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        AsyncClient(request => Task.FromResult(handler(request)));

    /// <summary>A TMDB client whose answers a test can hold back, to decide in which order the sources answer.</summary>
    internal static HttpClient AsyncClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(new StubHandler(handler))
        {
            BaseAddress = new Uri("https://api.themoviedb.org/3/")
        };

    internal static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request);
    }
}
