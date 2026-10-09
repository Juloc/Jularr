using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexWorkMatcherTests
{
    [TestMethod]
    public async Task ConfirmedTmdbIdentityMatchesCanonicalMovie()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var work = await works.CreateWorkAsync(
            WorkMediaType.Movie, "Dune", 2021, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            work.Id, WorkMediaType.Movie, "tmdb", "438631",
            1.0, "verified TMDB", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);

        var plex = Item("movie", new PlexExternalId("tmdb", "438631"));
        var id = await new PlexWorkMatcher(db).ResolveWorkIdAsync(
            plex, CancellationToken.None);

        Assert.AreEqual(work.Id, id);
    }

    [TestMethod]
    public async Task UnreviewedOrTitleOnlyMatchesDoNotCreateExternalPlaybackTarget()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var work = await works.CreateWorkAsync(
            WorkMediaType.Series, "Matching Title", null, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            work.Id, WorkMediaType.Series, "tvdb", "99",
            0.1, "unreviewed", false, false,
            MappingReviewState.Unverified, CancellationToken.None);

        var matcher = new PlexWorkMatcher(db);
        Assert.IsNull(await matcher.ResolveWorkIdAsync(
            Item("show", new PlexExternalId("tvdb", "99")),
            CancellationToken.None));
        Assert.IsNull(await matcher.ResolveWorkIdAsync(
            Item("show"), CancellationToken.None));
        Assert.IsNull(await matcher.ResolveWorkIdAsync(
            Item("episode", new PlexExternalId("tvdb", "99")),
            CancellationToken.None));
    }

    [TestMethod]
    public async Task ContradictoryExternalIdsDoNotGuessWhichWorkToOpen()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var first = await works.CreateWorkAsync(
            WorkMediaType.Movie, "First", null, CancellationToken.None);
        var second = await works.CreateWorkAsync(
            WorkMediaType.Movie, "Second", null, CancellationToken.None);

        await works.LinkExternalIdentityAsync(
            first.Id, WorkMediaType.Movie, "tmdb", "100",
            1.0, "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            second.Id, WorkMediaType.Movie, "imdb", "tt200",
            1.0, "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);

        var id = await new PlexWorkMatcher(db).ResolveWorkIdAsync(
            Item("movie",
                new PlexExternalId("tmdb", "100"),
                new PlexExternalId("imdb", "tt200")),
            CancellationToken.None);

        Assert.IsNull(id);
    }

    [TestMethod]
    public async Task PlexShowMatchesAnimeUnlessSeriesIdentityIsAmbiguous()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var anime = await works.CreateWorkAsync(
            WorkMediaType.Anime, "Anime", null, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            anime.Id, WorkMediaType.Anime, "tmdb", "300",
            1.0, "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);

        var matcher = new PlexWorkMatcher(db);
        var plex = Item("show", new PlexExternalId("tmdb", "300"));
        Assert.AreEqual(
            anime.Id, await matcher.ResolveWorkIdAsync(
                plex, CancellationToken.None));

        var series = await works.CreateWorkAsync(
            WorkMediaType.Series, "Unrelated series", null, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            series.Id, WorkMediaType.Series, "tmdb", "300",
            1.0, "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);

        Assert.IsNull(await matcher.ResolveWorkIdAsync(
            plex, CancellationToken.None));
    }

    [TestMethod]
    public async Task PageMatching_ReturnsOneResultPerItemAndRejectsEpisodes()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var movie = await works.CreateWorkAsync(
            WorkMediaType.Movie, "Dune", 2021, CancellationToken.None);
        var show = await works.CreateWorkAsync(
            WorkMediaType.Series, "The Series", null, CancellationToken.None);

        await works.LinkExternalIdentityAsync(
            movie.Id, WorkMediaType.Movie, "tmdb", "438631",
            1.0, "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            show.Id, WorkMediaType.Series, "tvdb", "88",
            1.0, "confirmed", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);

        var matcher = new PlexWorkMatcher(db);
        var result = await matcher.ResolvePageAsync(
            [
                Item("movie", new PlexExternalId("tmdb", "438631")),
                Item("show", new PlexExternalId("tvdb", "88")),
                Item("episode", new PlexExternalId("tvdb", "88")),
                Item("movie", new PlexExternalId("tmdb", "missing"))
            ], CancellationToken.None);

        Assert.AreEqual(4, result.Count);
        Assert.AreEqual(movie.Id, result[0].WorkId);
        Assert.AreEqual(show.Id, result[1].WorkId);
        Assert.IsNull(result[2].WorkId);
        Assert.IsNull(result[3].WorkId);
    }

    private static PlexLibraryItem Item(
        string type,
        params PlexExternalId[] identities) =>
        new("123", type, "Matching Title", 2021, identities);
}
