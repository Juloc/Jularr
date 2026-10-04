using System.Net;
using System.Text;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Providers;
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
                      "release_date": "1999-10-15",
                      "genre_ids": [18],
                      "vote_average": 8.4
                    }
                  ]
                }
                """);
        });
        var provider = Provider(db, client);

        var first = await provider.SearchAsync(
            TmdbDiscoveryMediaType.Movie, "Fight Club", 10, "Drama", CancellationToken.None);
        var second = await provider.SearchAsync(
            TmdbDiscoveryMediaType.Movie, "Fight Club", 10, "Drama", CancellationToken.None);

        Assert.AreEqual(1, calls, "The second identical provider request must hit ProviderResponseCache.");
        Assert.AreEqual(1, first.Count);
        Assert.AreEqual("550", first[0].ExternalId);
        Assert.AreEqual("movie", first[0].Category);
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

        var first = await provider.EnsureCanonicalWorkAsync(
            TmdbDiscoveryMediaType.Movie, "550", CancellationToken.None);
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

    private static TmdbDiscoveryProvider Provider(
        Jularr.Web.Data.AppDbContext db,
        HttpClient client)
    {
        var clock = TimeProvider.System;
        var configuration = new ConfigurationManager
        {
            ["Providers:Tmdb:ApiKey"] = "test-key"
        };
        var works = new WorkService(db);
        var structure = new WorkStructureService(db);
        return new TmdbDiscoveryProvider(
            client,
            configuration,
            new ProviderExecutor(
                new ProviderRateLimiter(),
                new ProviderHealthTracker(clock),
                clock,
                NullLogger<ProviderExecutor>.Instance),
            new ProviderResponseCache(clock),
            works,
            structure,
            db,
            new LegacyWorkBridge(db, works, structure));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new StubHandler(handler))
        {
            BaseAddress = new Uri("https://api.themoviedb.org/3/")
        };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
