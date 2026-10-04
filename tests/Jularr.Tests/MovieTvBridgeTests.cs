using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Tv;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Movie (#593) and TV (#594) per-type records bridge to the universal media core through the
/// <c>WorkSourceKind.Movie</c> / <c>WorkSourceKind.Series</c> source links, mirroring their provider
/// identities. TV reuses the universal season/episode structure.
/// </summary>
[TestClass]
public sealed class MovieTvBridgeTests
{
    private static LegacyWorkBridge Bridge(Jularr.Web.Data.AppDbContext db) =>
        new(db, new WorkService(db), new WorkStructureService(db));

    [TestMethod]
    public async Task MovieBridgeIsIdempotentAndMirrorsIdentities()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var movie = new Movie { Key = "inception:2010", Title = "Inception", Year = 2010, TmdbId = "27205", ImdbId = "tt1375666" };
        db.Movies.Add(movie);
        await db.SaveChangesAsync();

        var bridge = Bridge(db);
        var first = await bridge.EnsureWorkForMovieAsync(movie, CancellationToken.None);
        var second = await bridge.EnsureWorkForMovieAsync(movie, CancellationToken.None);

        Assert.AreEqual(first, second, "Bridging the same movie twice must resolve to the same work.");
        Assert.AreEqual(1, db.Works.Count());

        var query = new WorkQueryService(db);
        var work = await query.GetWorkAsync(first, CancellationToken.None);
        Assert.AreEqual(WorkMediaType.Movie, work!.MediaType);
        Assert.AreEqual(first, await query.ResolveWorkForSourceAsync(WorkSourceKind.Movie, movie.Id, CancellationToken.None));
        Assert.AreEqual(first, await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Movie, "tmdb", "27205", CancellationToken.None));
        Assert.AreEqual(first, await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Movie, "imdb", "tt1375666", CancellationToken.None));
    }

    [TestMethod]
    public async Task LegacyVideoRowsReuseAnExistingCanonicalTmdbWork()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var movieWork = await works.EnsureWorkByExternalIdentityAsync(
            WorkMediaType.Movie, "tmdb", "27205", "Inception", 2010, CancellationToken.None);
        var seriesWork = await works.EnsureWorkByExternalIdentityAsync(
            WorkMediaType.Series, "tmdb", "1396", "Breaking Bad", 2008, CancellationToken.None);

        var movie = new Movie { Key = "inception:2010", Title = "Inception", Year = 2010, TmdbId = "27205" };
        var series = new TvSeries { Key = "breakingbad:2008", Title = "Breaking Bad", Year = 2008, TmdbId = "1396" };
        db.Movies.Add(movie);
        db.TvSeries.Add(series);
        await db.SaveChangesAsync();

        var bridge = Bridge(db);
        Assert.AreEqual(movieWork.Id, await bridge.EnsureWorkForMovieAsync(movie, CancellationToken.None));
        Assert.AreEqual(seriesWork.Id, await bridge.EnsureWorkForSeriesAsync(series, CancellationToken.None));

        Assert.AreEqual(2, await db.Works.CountAsync(), "Import must not create a second Work next to the provider-resolved Work.");
        Assert.AreEqual(movieWork.Id, await new WorkQueryService(db).ResolveWorkForSourceAsync(
            WorkSourceKind.Movie, movie.Id, CancellationToken.None));
        Assert.AreEqual(seriesWork.Id, await new WorkQueryService(db).ResolveWorkForSourceAsync(
            WorkSourceKind.Series, series.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task SeriesBridgeMirrorsIdentitiesAndReusesSeasonEpisodeStructure()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = new TvSeries { Key = "breakingbad:2008", Title = "Breaking Bad", Year = 2008, TmdbId = "1396", TvdbId = "81189" };
        db.TvSeries.Add(series);
        await db.SaveChangesAsync();

        var bridge = Bridge(db);
        var workId = await bridge.EnsureWorkForSeriesAsync(series, CancellationToken.None);

        var query = new WorkQueryService(db);
        Assert.AreEqual(WorkMediaType.Series, (await query.GetWorkAsync(workId, CancellationToken.None))!.MediaType);
        Assert.AreEqual(workId, await query.ResolveWorkForSourceAsync(WorkSourceKind.Series, series.Id, CancellationToken.None));
        Assert.AreEqual(workId, await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Series, "tmdb", "1396", CancellationToken.None));
        Assert.AreEqual(workId, await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Series, "tvdb", "81189", CancellationToken.None));

        // TV reuses the universal season/episode structure rather than a per-type episode table.
        var structure = new WorkStructureService(db);
        var season = await structure.AddOrUpdateSeasonAsync(workId, 1, null, CancellationToken.None);
        await structure.AddOrUpdateEpisodeAsync(workId, 1, 2, null, false, "Cat's in the Bag", null, season.Id, CancellationToken.None);

        Assert.AreEqual(1, await db.WorkSeasons.CountAsync(x => x.WorkId == workId));
        var episode = await db.WorkEpisodes.SingleAsync(x => x.WorkId == workId);
        Assert.AreEqual(1, episode.SeasonNumber);
        Assert.AreEqual(2, episode.EpisodeNumber);
    }
}
