using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Music;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// The one canonical Monitoring state: explicit decisions with Inherit as the absence of one, inheritance through the canonical structure, "future" as an
/// ordinary set of decisions, and relation sources that reach Works without becoming Wanted items themselves.
/// </summary>
[TestClass]
public sealed class CanonicalMonitoringTests
{
    private sealed record Series(Guid WorkId, Guid[] SeasonIds, Guid[][] EpisodeIds);

    private static MonitoringCommands Commands(AppDbContext db) => new(db, TimeProvider.System);

    private static async Task<Series> AddSeriesAsync(AppDbContext db, string title, params int[] episodesPerSeason)
    {
        var works = new WorkService(db);
        var structure = new WorkStructureService(db);
        var work = await works.CreateWorkAsync(WorkMediaType.Series, title, 2024, CancellationToken.None);
        var seasons = new List<Guid>();
        var episodes = new List<Guid[]>();
        for (var season = 1; season <= episodesPerSeason.Length; season++)
        {
            var seasonId = (await structure.AddOrUpdateSeasonAsync(work.Id, season, null, CancellationToken.None)).Id;
            seasons.Add(seasonId);
            var ids = new List<Guid>();
            for (var number = 1; number <= episodesPerSeason[season - 1]; number++)
            {
                ids.Add((await structure.AddOrUpdateEpisodeAsync(work.Id, season, number, null, false, null, DateTime.UtcNow.AddDays(-10), seasonId, CancellationToken.None)).Id);
            }

            episodes.Add([.. ids]);
        }

        return new Series(work.Id, [.. seasons], [.. episodes]);
    }

    private static async Task AddCreditAsync(AppDbContext db, Guid workId, string personId, WorkCreditKind kind, string? role)
    {
        db.Set<WorkCredit>().Add(new WorkCredit { WorkId = workId, Kind = kind, Position = 0, Name = personId, Role = role, Source = "tmdb", ProviderPersonId = personId, FetchedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ADecisionOnANodeBeatsItsAncestorsAndAMissingDecisionInheritsDownToTheWork()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = await AddSeriesAsync(db, "Harbor", 3, 2);
        var commands = Commands(db);
        var resolver = new MonitoringResolver(db);

        Assert.IsFalse((await resolver.LoadAsync(series.WorkId, CancellationToken.None)).IsMonitored(series.EpisodeIds[0][0], series.SeasonIds[0]), "Nothing decided and no relation: not monitored.");

        await commands.SetAsync(MonitoringTargetKind.Work, series.WorkId, true, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Season, series.SeasonIds[0], false, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Episode, series.EpisodeIds[0][1], true, CancellationToken.None);
        var view = await resolver.LoadAsync(series.WorkId, CancellationToken.None);

        Assert.IsTrue(view.IsMonitored(series.EpisodeIds[1][0], series.SeasonIds[1]), "The second season inherits the Work.");
        Assert.IsFalse(view.IsMonitored(series.EpisodeIds[0][0], series.SeasonIds[0]), "The first season switched its episodes off.");
        Assert.IsTrue(view.IsMonitored(series.EpisodeIds[0][1], series.SeasonIds[0]), "An episode decides for itself.");

        await commands.SetAsync(MonitoringTargetKind.Episode, series.EpisodeIds[0][1], null, CancellationToken.None);
        Assert.IsFalse((await resolver.LoadAsync(series.WorkId, CancellationToken.None)).IsMonitored(series.EpisodeIds[0][1], series.SeasonIds[0]), "Inherit goes back to the season's decision.");
        Assert.IsNull(await commands.SetAsync(MonitoringTargetKind.Season, Guid.NewGuid(), true, CancellationToken.None), "An unknown node is reported, not created.");
    }

    [TestMethod]
    public async Task ASeasonOrWorkDecisionTakesItsChildrenWithItSoMonitorAllAndUnmonitorAllAreOneCall()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = await AddSeriesAsync(db, "Harbor", 3, 2);
        var commands = Commands(db);
        var resolver = new MonitoringResolver(db);
        await commands.SetManyAsync(MonitoringTargetKind.Episode, series.EpisodeIds[0], true, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Season, series.SeasonIds[0], false, CancellationToken.None);

        Assert.AreEqual(1, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkMonitoring" WHERE "WorkId" = {series.WorkId}""").SingleAsync(), "The season's own decision replaced the episode decisions.");

        await commands.SetAsync(MonitoringTargetKind.Work, series.WorkId, true, CancellationToken.None);
        var view = await resolver.LoadAsync(series.WorkId, CancellationToken.None);

        Assert.IsTrue(series.EpisodeIds.SelectMany(ids => ids).All(id => view.IsMonitored(id)));
        Assert.AreEqual(1, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkMonitoring" WHERE "WorkId" = {series.WorkId}""").SingleAsync());
    }

    [TestMethod]
    public async Task AFailingStepRollsTheWholeCommandBack()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = await AddSeriesAsync(db, "Harbor", 2);
        var commands = Commands(db);
        await commands.SetManyAsync(MonitoringTargetKind.Episode, series.EpisodeIds[0], true, CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION fail_monitoring_delete() RETURNS trigger LANGUAGE plpgsql AS $body$ BEGIN RAISE EXCEPTION 'blocked'; END $body$;
            CREATE TRIGGER fail_monitoring_delete BEFORE DELETE ON "WorkMonitoring" FOR EACH ROW EXECUTE FUNCTION fail_monitoring_delete();
            """);

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => commands.SetAsync(MonitoringTargetKind.Work, series.WorkId, true, CancellationToken.None));

        var workDecisions = await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkMonitoring" WHERE "TargetId" = {series.WorkId}""").SingleAsync();
        Assert.AreEqual(0, workDecisions, "The Work's own decision was written before the episode decisions could be replaced, so it must have been rolled back with them.");
    }

    [TestMethod]
    public async Task FutureMonitorsTheWorkSwitchesTodaysEpisodesOffAndLeavesTheSeasonsAloneSoLaterEpisodesInherit()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = await AddSeriesAsync(db, "Harbor", 250, 250);
        var commands = Commands(db);
        await commands.SetAsync(MonitoringTargetKind.Season, series.SeasonIds[0], true, CancellationToken.None);

        await commands.FutureAsync(series.WorkId, CancellationToken.None);
        var later = await new WorkStructureService(db).AddOrUpdateEpisodeAsync(series.WorkId, 2, 251, null, false, null, DateTime.UtcNow.AddDays(7), series.SeasonIds[1], CancellationToken.None);
        var view = await new MonitoringResolver(db).LoadAsync(series.WorkId, CancellationToken.None);

        Assert.IsTrue(view.IsWorkMonitored);
        Assert.IsTrue(series.EpisodeIds.SelectMany(ids => ids).All(id => view.DecisionOf(id) == false), "Every known episode is off by its own decision.");
        Assert.IsFalse(series.SeasonIds.Any(id => view.DecisionOf(id) is not null), "No season carries a decision: a switched-off season would switch off its future episodes.");
        Assert.IsTrue(view.IsMonitored(later.Id, series.SeasonIds[1]), "An episode discovered later has no decision and inherits the Work.");
        Assert.AreEqual(501, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkMonitoring" WHERE "WorkId" = {series.WorkId}""").SingleAsync(), "The Work and its 500 known episodes, written by one command.");
    }

    [TestMethod]
    public async Task AMonitoredPersonReachesOnlyTheWorksOfTheAllowedRolesAndAWorkCanOverrideIt()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var directed = await works.CreateWorkAsync(WorkMediaType.Movie, "Directed", 2020, CancellationToken.None);
        var acted = await works.CreateWorkAsync(WorkMediaType.Movie, "Acted", 2021, CancellationToken.None);
        var overridden = await works.CreateWorkAsync(WorkMediaType.Movie, "Directed but off", 2022, CancellationToken.None);
        await AddCreditAsync(db, directed.Id, "p1", WorkCreditKind.Crew, "Director");
        await AddCreditAsync(db, acted.Id, "p1", WorkCreditKind.Cast, "Captain");
        await AddCreditAsync(db, overridden.Id, "p1", WorkCreditKind.Crew, "Director");
        var commands = Commands(db);
        var resolver = new MonitoringResolver(db);

        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Person, "p1", "Pat", ["director", "Writer"], "owner"), true, false, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Work, overridden.Id, false, CancellationToken.None);
        var views = await resolver.LoadManyAsync([directed.Id, acted.Id, overridden.Id], CancellationToken.None);

        Assert.IsTrue(views[directed.Id].IsWorkMonitored, "A directing credit matches the allowed role.");
        Assert.IsFalse(views[acted.Id].IsWorkMonitored, "An acting credit is not an allowed role.");
        Assert.IsFalse(views[overridden.Id].IsWorkMonitored, "The Work's own decision beats the relation.");
        CollectionAssert.AreEqual(new[] { directed.Id }, (await resolver.MonitoredWorkIdsAsync(WorkMediaType.Movie, Guid.Empty, 50, CancellationToken.None)).ToArray());

        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Person, "p1", "Pat", ["Actor"], "owner"), true, false, CancellationToken.None);
        Assert.IsTrue((await resolver.LoadAsync(acted.Id, CancellationToken.None)).IsWorkMonitored, "Actor reaches every cast credit; the role list was replaced, not added to.");
        Assert.IsFalse((await resolver.LoadAsync(directed.Id, CancellationToken.None)).IsWorkMonitored);

        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Person, "p1", "Pat", null, "owner"), false, false, CancellationToken.None);
        Assert.AreEqual(0, (await resolver.MonitoredWorkIdsAsync(WorkMediaType.Movie, Guid.Empty, 50, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task SeveralSourcesThatReachOneWorkListItOnceAndAStudioAndACollectionAreSourcesToo()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var movie = await works.CreateWorkAsync(WorkMediaType.Movie, "Shared", 2020, CancellationToken.None);
        var other = await works.CreateWorkAsync(WorkMediaType.Movie, "Other", 2021, CancellationToken.None);
        await AddCreditAsync(db, movie.Id, "p1", WorkCreditKind.Crew, "Writer");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "WorkMetadataFacts" ("WorkId", "OriginalLanguage", "Studios", "ProductionCountries", "UpdatedAt") VALUES ({movie.Id}, 'en', ARRAY['Studio X'], ARRAY[]::text[], now())""");
        var collectionId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Collections" ("Id", "ProfileId", "Kind", "Name", "SortOrder", "CreatedAt", "UpdatedAt") VALUES ({collectionId}, 'owner', 0, 'Saga', 0, now(), now())""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "CollectionItems" ("Id", "CollectionId", "WorkId", "Source", "Position", "AddedAt") VALUES (gen_random_uuid(), {collectionId}, {other.Id}, 0, 0, now())""");
        var commands = Commands(db);

        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Person, "p1", "Pat", null, "owner"), true, false, CancellationToken.None);
        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Studio, "studio x", "Studio X", null, "owner"), true, false, CancellationToken.None);
        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Collection, collectionId.ToString(), "Saga", null, "owner"), true, false, CancellationToken.None);
        var monitored = await new MonitoringResolver(db).MonitoredWorkIdsAsync(WorkMediaType.Movie, Guid.Empty, 50, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { movie.Id, other.Id }, monitored.ToArray(), "Two sources reach the shared movie and it is listed once; the collection reaches the other one.");
    }

    [TestMethod]
    public async Task AMonitoredArtistReachesItsAlbumsAndTracksAreMonitoredIndividually()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var album = await works.CreateWorkAsync(WorkMediaType.Music, "Tides", 2019, CancellationToken.None);
        var later = await works.CreateWorkAsync(WorkMediaType.Music, "Undertow", 2025, CancellationToken.None);
        var artist = new MusicArtist { Name = "Waves", SortName = "Waves", MusicBrainzId = Guid.NewGuid().ToString() };
        db.MusicArtists.Add(artist);
        db.MusicAlbums.Add(new MusicAlbum { WorkId = album.Id, ArtistId = artist.Id, Type = MusicAlbumType.Album });
        await db.SaveChangesAsync();
        var artistId = artist.Id;
        var commands = Commands(db);

        await commands.SetRelationAsync(new MonitoringRelationSource(MonitoringRelationKind.Artist, artistId.ToString(), "Waves", null, "owner"), true, onlyFuture: true, CancellationToken.None);
        db.MusicAlbums.Add(new MusicAlbum { WorkId = later.Id, ArtistId = artistId, Type = MusicAlbumType.Album });
        await db.SaveChangesAsync();
        var resolver = new MonitoringResolver(db);

        Assert.IsFalse((await resolver.LoadAsync(album.Id, CancellationToken.None)).IsWorkMonitored, "Future: the album that exists today is switched off by a decision.");
        Assert.IsTrue((await resolver.LoadAsync(later.Id, CancellationToken.None)).IsWorkMonitored, "Future: an album that appears later is reached by the artist.");

        var track = await db.Set<WorkTrack>().AddAsync(new WorkTrack { WorkId = later.Id, Disc = 1, Number = 1, Title = "Rip" });
        await db.SaveChangesAsync();
        await commands.SetAsync(MonitoringTargetKind.Track, track.Entity.Id, false, CancellationToken.None);
        var view = await resolver.LoadAsync(later.Id, CancellationToken.None);

        Assert.IsFalse(view.IsMonitored(track.Entity.Id), "One track of a monitored album can be switched off by itself.");
        Assert.IsTrue(view.IsWorkMonitored);
    }

    [TestMethod]
    public async Task AWorkWithOnlyANodeSwitchedOnCountsAsMonitoredForTheWantedPass()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = await AddSeriesAsync(db, "Harbor", 2);
        await AddSeriesAsync(db, "Quiet", 2);
        var commands = Commands(db);
        await commands.SetAsync(MonitoringTargetKind.Work, series.WorkId, false, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Episode, series.EpisodeIds[0][0], true, CancellationToken.None);

        var monitored = await new MonitoringResolver(db).MonitoredWorkIdsAsync(WorkMediaType.Series, Guid.Empty, 50, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { series.WorkId }, monitored.ToArray());
    }

    [TestMethod]
    public async Task ARequestChoiceBecomesOrdinaryDecisionsOfTheWork()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var series = await AddSeriesAsync(db, "Harbor", 2, 2);
        var resolver = new MonitoringResolver(db);

        await MonitoringTestSupport.ApplyAsync(db, series.WorkId, VideoRequestScope.AllCurrentAndFuture, future: true);
        var all = await resolver.LoadAsync(series.WorkId, CancellationToken.None);
        Assert.IsTrue(all.IsWorkMonitored && !all.HasNodeDecisions, "All is the Work monitored and nothing else.");

        await MonitoringTestSupport.ApplyAsync(db, series.WorkId, VideoRequestScope.FutureOnly, future: true);
        var future = await resolver.LoadAsync(series.WorkId, CancellationToken.None);
        Assert.IsTrue(future.IsWorkMonitored);
        Assert.IsFalse(future.IsMonitored(series.EpisodeIds[0][0], series.SeasonIds[0]), "Future switches the episodes that exist off and leaves the Work on.");

        await MonitoringTestSupport.ApplyAsync(db, series.WorkId, VideoRequestScope.Custom, [series.EpisodeIds[1][1]]);
        var custom = await resolver.LoadAsync(series.WorkId, CancellationToken.None);
        Assert.IsFalse(custom.IsWorkMonitored);
        Assert.IsTrue(custom.IsMonitored(series.EpisodeIds[1][1], series.SeasonIds[1]));
        Assert.IsFalse(custom.IsMonitored(series.EpisodeIds[1][0], series.SeasonIds[1]));
    }

    [TestMethod]
    public async Task MergingWorksKeepsTheSurvivorsOwnDecisionAndOtherwiseTakesTheAbsorbedOne()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var commands = Commands(db);
        var decided = await works.CreateWorkAsync(WorkMediaType.Movie, "Decided", 2020, CancellationToken.None);
        var absorbedA = await works.CreateWorkAsync(WorkMediaType.Movie, "Absorbed A", 2020, CancellationToken.None);
        var undecided = await works.CreateWorkAsync(WorkMediaType.Movie, "Undecided", 2021, CancellationToken.None);
        var absorbedB = await works.CreateWorkAsync(WorkMediaType.Movie, "Absorbed B", 2021, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Work, decided.Id, false, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Work, absorbedA.Id, true, CancellationToken.None);
        await commands.SetAsync(MonitoringTargetKind.Work, absorbedB.Id, true, CancellationToken.None);

        await works.MergeWorksAsync(decided.Id, absorbedA.Id, "test", CancellationToken.None);
        await works.MergeWorksAsync(undecided.Id, absorbedB.Id, "test", CancellationToken.None);

        var resolver = new MonitoringResolver(db);
        Assert.IsFalse((await resolver.LoadAsync(decided.Id, CancellationToken.None)).IsWorkMonitored, "The survivor decided for itself.");
        Assert.IsTrue((await resolver.LoadAsync(undecided.Id, CancellationToken.None)).IsWorkMonitored, "The survivor had no decision and takes the absorbed one.");
        Assert.AreEqual(2, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkMonitoring" """).SingleAsync(), "Nothing of the absorbed Works is left behind.");
    }
}
