using System.Net;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class TmdbWatchProvidersTests
{
    [TestMethod]
    public async Task ConfirmedMovieUsesExactRegionAndCachesSourceWithJustWatchAttribution()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var movie = await works.CreateWorkAsync(
            WorkMediaType.Movie, "Sample movie", 2020, CancellationToken.None);
        Assert.IsTrue(await works.LinkExternalIdentityAsync(
            movie.Id, WorkMediaType.Movie, "tmdb", "550", 1,
            "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None));

        var requests = 0;
        using var http = TmdbDiscoveryTests.Client(request =>
        {
            requests++;
            Assert.AreEqual("/3/movie/550/watch/providers", request.RequestUri!.AbsolutePath);
            return TmdbDiscoveryTests.Json(
                """
                {
                  "results":{
                    "DE":{
                      "link":"https://www.themoviedb.org/movie/550/watch?locale=DE",
                      "flatrate":[{"provider_id":8,"provider_name":"Streaming Example","display_priority":1}],
                      "rent":[{"provider_id":8,"provider_name":"Streaming Example","display_priority":1}],
                      "buy":[{"provider_id":4,"provider_name":"Digital Store","display_priority":4}]
                    },
                    "US":{
                      "link":"https://www.themoviedb.org/movie/550/watch?locale=US",
                      "free":[{"provider_id":12,"provider_name":"Free Channel","display_priority":3}]
                    }
                  }
                }
                """);
        });
        var provider = TmdbDiscoveryTests.Provider(db, http);
        var german = await provider.GetWatchAvailabilityAsync(
            movie.Id, WorkMediaType.Movie, "DE", CancellationToken.None);
        var again = await provider.GetWatchAvailabilityAsync(
            movie.Id, WorkMediaType.Movie, "DE", CancellationToken.None);

        Assert.IsNotNull(german);
        Assert.IsNotNull(again);
        Assert.AreEqual(german.WatchPage, again.WatchPage);
        CollectionAssert.AreEqual(
            german.Offers.ToArray(), again.Offers.ToArray());
        Assert.AreEqual("DE", german.Region);
        Assert.AreEqual("JustWatch", german.Attribution);
        Assert.AreEqual("www.themoviedb.org", german.WatchPage.Host);
        Assert.AreEqual(3, german.Offers.Count);
        Assert.AreEqual("streaming", german.Offers[0].Category);
        Assert.AreEqual("rent", german.Offers[1].Category);
        Assert.AreEqual("buy", german.Offers[2].Category);
        Assert.AreEqual(1, requests);
        Assert.AreEqual(1, await db.Works.CountAsync());
    }

    [TestMethod]
    public async Task WrongRegionAndUnconfirmedOrAmbiguousIdentityNeverGuessAvailability()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var movie = await works.CreateWorkAsync(
            WorkMediaType.Movie, "Unreviewed", 2021, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            movie.Id, WorkMediaType.Movie, "tmdb", "222", 0.2,
            "unreviewed", true, false,
            MappingReviewState.Unverified, CancellationToken.None);

        var requests = 0;
        using var http = TmdbDiscoveryTests.Client(_ =>
        {
            requests++;
            return TmdbDiscoveryTests.Json(
                """{"results":{"US":{"link":"https://www.themoviedb.org/movie/222/watch?locale=US","flatrate":[{"provider_id":8,"provider_name":"Example"}]}}}""");
        });
        var provider = TmdbDiscoveryTests.Provider(db, http);
        Assert.IsNull(await provider.GetWatchAvailabilityAsync(
            movie.Id, WorkMediaType.Movie, "DE", CancellationToken.None));
        Assert.AreEqual(0, requests);

        await works.LinkExternalIdentityAsync(
            movie.Id, WorkMediaType.Movie, "tmdb", "222", 1,
            "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);
        Assert.IsNull(await provider.GetWatchAvailabilityAsync(
            movie.Id, WorkMediaType.Movie, "DE", CancellationToken.None));
        Assert.AreEqual(1, requests);

        await works.LinkExternalIdentityAsync(
            movie.Id, WorkMediaType.Movie, "tmdb", "333", 1,
            "conflicting", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);
        Assert.IsNull(await provider.GetWatchAvailabilityAsync(
            movie.Id, WorkMediaType.Movie, "DE", CancellationToken.None));
        Assert.AreEqual(1, requests);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            provider.GetWatchAvailabilityAsync(
                movie.Id, WorkMediaType.Movie, "de", CancellationToken.None));
    }

    [TestMethod]
    public void MaliciousWatchLinksAndUnsupportedEntriesAreRejected()
    {
        using var invalidHost = System.Text.Json.JsonDocument.Parse(
            """{"results":{"DE":{"link":"https://attacker.test/watch","flatrate":[{"provider_id":8,"provider_name":"Example"}]}}}""");
        Assert.IsNull(TmdbDiscoveryProvider.ReadWatchAvailability(
            invalidHost.RootElement, "movie", "550", "DE"));

        using var wrongWork = System.Text.Json.JsonDocument.Parse(
            """{"results":{"DE":{"link":"https://www.themoviedb.org/movie/551/watch?locale=DE","flatrate":[{"provider_id":8,"provider_name":"Example"}]}}}""");
        Assert.IsNull(TmdbDiscoveryProvider.ReadWatchAvailability(
            wrongWork.RootElement, "movie", "550", "DE"));

        using var noOffers = System.Text.Json.JsonDocument.Parse(
            """{"results":{"DE":{"link":"https://www.themoviedb.org/movie/550/watch?locale=DE","flatrate":[]}}}""");
        Assert.IsNull(TmdbDiscoveryProvider.ReadWatchAvailability(
            noOffers.RootElement, "movie", "550", "DE"));
    }
}
