using System.Diagnostics;
using System.Net;
using Jularr.Web.Data;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// How Discover loads titles (#595): every source on its own, fixed order whatever answers first, a first paint that does not wait for the slowest
/// source and a follow-up that waits for the next arrival, failure isolation, a retry that asks again, hidden types that are never called, and the
/// library overlay that prefers persisted Work metadata.
/// </summary>
[TestClass]
public sealed class DiscoveryCoordinatorTests
{
    private static readonly IReadOnlySet<WorkMediaType> EveryType = new HashSet<WorkMediaType>(WorkMediaTypes.All);

    private static DiscoveryAudience Audience(params WorkMediaType[] visible) => new("profile", false, visible.Length == 0 ? EveryType : new HashSet<WorkMediaType>(visible));

    private static DiscoveryRequest Request(DiscoveryCategory category, DiscoveryMode mode = DiscoveryMode.Trending, string query = "") => new(query, category, mode);

    private static string Results(string prefix, int count, bool movie)
    {
        var title = movie ? "title" : "name";
        var date = movie ? "release_date" : "first_air_date";
        var rows = Enumerable.Range(1, count).Select(index =>
            $"{{\"id\":{index + (movie ? 100 : 200)},\"{title}\":\"{prefix} {index}\",\"overview\":\"x\",\"poster_path\":\"/p{index}.jpg\","
            + $"\"{date}\":\"2024-01-01\",\"genre_ids\":[],\"vote_average\":7.1}}");
        return "{\"results\":[" + string.Join(',', rows) + "]}";
    }

    private static async Task<(DiscoveryCoordinator Coordinator, AppDbContext Db)> CoordinatorAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> tmdbHandler, DiscoveryTestSupport.MovableClock? clock = null)
    {
        var db = await MediaCoreTestSupport.CreateDbAsync();
        var tmdb = TmdbDiscoveryTests.Provider(db, TmdbDiscoveryTests.AsyncClient(tmdbHandler));
        var coordinator = new DiscoveryCoordinator(tmdb, null!, db, DiscoveryTestSupport.Flights(clock, providers: [tmdb]), NullLogger<DiscoveryCoordinator>.Instance);
        return (coordinator, db);
    }

    private static Task<HttpResponseMessage> Answer(string json) => Task.FromResult(TmdbDiscoveryTests.Json(json));

    [TestMethod]
    public async Task ASourceThatFailsOnlyDegradesItsOwnBatchAndIsNeverAnEmptyAnswer()
    {
        var (coordinator, db) = await CoordinatorAsync(request => request.RequestUri!.AbsolutePath.Contains("/movie", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : Answer(Results("Series", 2, movie: false)));
        await using var database = db;

        var load = await coordinator.LoadAsync([Request(DiscoveryCategory.Movie), Request(DiscoveryCategory.Series)], Audience(), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.AreEqual(DiscoverySourceState.Unavailable, load.Batches[0].Sources.Single().State);
        Assert.AreEqual(0, load.Batches[0].Items.Count);
        Assert.AreEqual(DiscoverySourceState.Ready, load.Batches[1].Sources.Single().State);
        CollectionAssert.AreEqual(new[] { "Series 1", "Series 2" }, load.Batches[1].Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(2, load.Settled);
        Assert.AreEqual(0, load.Pending);
    }

    [TestMethod]
    public async Task ARateLimitedSourceIsBusyAndNotAnOutage()
    {
        var (coordinator, db) = await CoordinatorAsync(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        await using var database = db;

        var load = await coordinator.LoadAsync([Request(DiscoveryCategory.Movie)], Audience(), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.AreEqual(DiscoverySourceState.Busy, load.Batches[0].Sources.Single().State);
    }

    [TestMethod]
    public async Task ATypeTheViewerMayNotBrowseIsNeverCalledAndHasNoSource()
    {
        var movieCalls = 0;
        var (coordinator, db) = await CoordinatorAsync(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/movie", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref movieCalls);
            }

            return Answer(Results("Series", 1, movie: false));
        });
        await using var database = db;

        var load = await coordinator.LoadAsync([Request(DiscoveryCategory.All)], Audience(WorkMediaType.Series), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { DiscoverySource.Series }, load.Batches[0].Sources.Select(source => source.Source).ToArray());
        Assert.AreEqual(0, movieCalls);
    }

    [TestMethod]
    public async Task TheOrderOfTitlesFollowsTheSourcesNotTheOrderTheyAnswered()
    {
        var movies = new TaskCompletionSource<HttpResponseMessage>();
        var (coordinator, db) = await CoordinatorAsync(request => request.RequestUri!.AbsolutePath.Contains("/movie", StringComparison.Ordinal)
            ? movies.Task
            : Answer(Results("Series", 2, movie: false)));
        await using var database = db;

        var loading = coordinator.LoadAsync([Request(DiscoveryCategory.All)], Audience(WorkMediaType.Movie, WorkMediaType.Series), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);
        await Task.Delay(300);
        Assert.IsFalse(loading.IsCompleted, "A load that waits for every source does not return while one is pending.");
        movies.SetResult(TmdbDiscoveryTests.Json(Results("Movie", 2, movie: true)));
        var load = await loading;

        CollectionAssert.AreEqual(new[] { "Movie 1", "Movie 2", "Series 1", "Series 2" }, load.Batches[0].Items.Select(item => item.Title).ToArray());
    }

    [TestMethod]
    public async Task AFirstPaintReturnsWhatIsReadyAndALaterLoadWaitsForTheNextArrival()
    {
        var movies = new TaskCompletionSource<HttpResponseMessage>();
        var (coordinator, db) = await CoordinatorAsync(request => request.RequestUri!.AbsolutePath.Contains("/movie", StringComparison.Ordinal)
            ? movies.Task
            : Answer(Results("Series", 1, movie: false)));
        await using var database = db;
        var requests = new[] { Request(DiscoveryCategory.Movie), Request(DiscoveryCategory.Series) };

        var first = await coordinator.LoadAsync(requests, Audience(), new DiscoveryWait(TimeSpan.FromMilliseconds(400)), CancellationToken.None);
        var followUp = coordinator.LoadAsync(requests, Audience(), new DiscoveryWait(TimeSpan.FromSeconds(20), first.Settled), CancellationToken.None);
        await Task.Delay(300);
        Assert.IsFalse(followUp.IsCompleted, "The follow-up holds the request open while nothing new has arrived.");
        var clock = Stopwatch.StartNew();
        movies.SetResult(TmdbDiscoveryTests.Json(Results("Movie", 1, movie: true)));
        var second = await followUp;

        Assert.AreEqual(DiscoverySourceState.Pending, first.Batches[0].Sources.Single().State);
        Assert.AreEqual(1, first.Settled);
        Assert.AreEqual(1, first.Pending);
        Assert.IsTrue(first.Batches[0].HasPending);
        Assert.AreEqual(DiscoverySourceState.Ready, second.Batches[0].Sources.Single().State);
        Assert.AreEqual(0, second.Pending);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), "It returns as soon as the next source has answered, not when the budget runs out.");
    }

    [TestMethod]
    public async Task AViewersRetryAsksAFailedSourceAgainWhileAPlainLoadReusesTheFailure()
    {
        var calls = 0;
        var healthy = false;
        var (coordinator, db) = await CoordinatorAsync(_ =>
        {
            Interlocked.Increment(ref calls);
            return healthy ? Answer(Results("Movie", 1, movie: true)) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        await using var database = db;
        var requests = new[] { Request(DiscoveryCategory.Movie) };
        var wait = new DiscoveryWait(TimeSpan.FromSeconds(10));

        var failed = await coordinator.LoadAsync(requests, Audience(), wait, CancellationToken.None);
        var callsAfterFailure = calls;
        var again = await coordinator.LoadAsync(requests, Audience(), wait, CancellationToken.None);
        healthy = true;
        var retried = await coordinator.LoadAsync(requests, Audience(), wait with { Refresh = new HashSet<DiscoverySource> { DiscoverySource.Movies } }, CancellationToken.None);

        Assert.AreEqual(DiscoverySourceState.Unavailable, failed.Batches[0].Sources.Single().State);
        Assert.AreEqual(callsAfterFailure, calls - 1, "Only the retry called the provider again; the plain load reused the failure.");
        Assert.AreEqual(DiscoverySourceState.Unavailable, again.Batches[0].Sources.Single().State);
        Assert.AreEqual(DiscoverySourceState.Ready, retried.Batches[0].Sources.Single().State);
    }

    [TestMethod]
    public async Task EveryRowOfARequestStartsItsProviderCallsAtTheSameTime()
    {
        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        var (coordinator, db) = await CoordinatorAsync(async request =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await gate.Task;
            Interlocked.Decrement(ref running);
            return TmdbDiscoveryTests.Json(Results("Row", 1, movie: request.RequestUri!.AbsolutePath.Contains("/movie", StringComparison.Ordinal)));
        });
        await using var database = db;

        var loading = coordinator.LoadAsync(
            [Request(DiscoveryCategory.Movie), Request(DiscoveryCategory.Movie, DiscoveryMode.Top), Request(DiscoveryCategory.Series), Request(DiscoveryCategory.Series, DiscoveryMode.Top)],
            Audience(),
            new DiscoveryWait(TimeSpan.FromSeconds(10)),
            CancellationToken.None);
        await Task.Delay(500);
        gate.SetResult();
        await loading;

        Assert.AreEqual(4, peak, "The rows of the landing wait for the provider side by side, not one after the other.");
    }

    [TestMethod]
    public async Task ATitleThatIsAlreadyAWorkShowsItsPersistedTitleAndPosterInsteadOfTheProvidersAndOthersKeepTheProvidersOwn()
    {
        var (coordinator, db) = await CoordinatorAsync(_ => Answer(Results("Provider", 2, movie: true)));
        await using var database = db;
        var seed = new LibraryCanonicalSeed(db);
        var work = await seed.AddWorkAsync(WorkMediaType.Movie, "Provider 1", 2024);
        await seed.AddVideoAsync(work, null);
        await new WorkService(db).LinkExternalIdentityAsync(work.Id, WorkMediaType.Movie, "tmdb", "101", 1.0, "test", true, false, MappingReviewState.Confirmed, CancellationToken.None);
        var store = new WorkMetadataStore(db);
        await store.ReplaceLocalizedFieldAsync(work.Id, "en", WorkLocalizedField.Title, ["Persisted Title"], "tmdb", "101", 10, DateTime.UtcNow, CancellationToken.None);
        var key = Jularr.Web.Features.Artwork.WorkArtworkCache.CacheKey(work.Id, WorkArtworkSlot.Poster, "", "tmdb", "/p.jpg");
        await store.UpsertArtworkAsync(work.Id, new WorkArtworkCandidate(WorkArtworkSlot.Poster, "", "/p.jpg", new Uri("https://image.tmdb.org/t/p/w780/p.jpg"), 2, 3, 5, 1), "tmdb", key, DateTime.UtcNow, CancellationToken.None);
        var load = await coordinator.LoadAsync([Request(DiscoveryCategory.Movie)], Audience(), new DiscoveryWait(TimeSpan.FromSeconds(10)), CancellationToken.None);

        var overlay = await coordinator.OverlayLocalStateAsync(load.Batches[0].Items, "viewer", CancellationToken.None);

        var local = overlay["tmdb:movie:101"];
        Assert.IsTrue(local.IsLocal);
        Assert.AreEqual(work.Id, local.LocalMediaId);
        Assert.AreEqual("Persisted Title", local.Title);
        StringAssert.StartsWith(local.CoverImageUrl, $"/works/{work.Id:D}/artwork/", "The poster comes from the local artwork endpoint, not the provider's CDN.");
        var transient = overlay["tmdb:movie:102"];
        Assert.IsFalse(transient.IsLocal);
        Assert.AreEqual("Provider 2", transient.Title);
        StringAssert.StartsWith(transient.CoverImageUrl, "https://image.tmdb.org/");
        Assert.AreEqual(1, await db.Works.CountAsync(), "Showing a candidate never creates a Work.");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = target;
        }
        while (value > current && Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
