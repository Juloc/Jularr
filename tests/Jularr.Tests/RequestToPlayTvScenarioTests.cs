using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>The Series lifecycle beyond the happy path: packs, multi-episode files, a download without a video, an offline library root and a later announced episode.</summary>
public sealed partial class RequestToPlayTvTests
{
    private static async Task<(string Name, Guid EpisodeId)[]> PlacedEpisodesAsync(VideoRequestToPlayWorld world, long workId) =>
        [.. (await (from asset in world.Db.MediaAssets.AsNoTracking()
                    join file in world.Db.StoredFiles.AsNoTracking() on asset.Id equals file.MediaAssetId
                    where asset.WorkId == workId && asset.WorkEpisodeId != null
                    orderby file.Path
                    select new { file.Path, EpisodeId = asset.WorkEpisodeId!.Value }).ToListAsync()).Select(row => (Path.GetFileName(row.Path), row.EpisodeId))];

    [TestMethod]
    public async Task ASeasonPackIsGrabbedOnceAndEachEpisodeGetsItsOwnFileUnderItsOwnNumber()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, Tmdb());
        const string Pack = "Severance.S01.1080p.WEB-DL.x264-PACK";
        world.Indexer.Publish(Pack);
        var episodeIds = await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        Assert.AreEqual(Pack, Assert.ContainsSingle(world.Sabnzbd.Grabs).NzbName, "The pack carries every wanted episode, so it is the one grab.");

        await world.CompleteDownloadAsync(request, Pack, [.. new[] { 1, 2, 3 }.Select(number => $"Severance.S01E{number:00}.1080p.WEB-DL.x264-PACK.mkv")]);
        await world.WantedPassAsync();
        await world.WantedPassAsync();

        var placed = await PlacedEpisodesAsync(world, work.Id);
        Assert.HasCount(3, placed);
        for (var number = 1; number <= 3; number++)
        {
            var episode = Assert.ContainsSingle(placed.Where(item => item.EpisodeId == episodeIds[number - 1]), $"Episode {number} has exactly one file.");
            StringAssert.Contains(episode.Name, $"S01E{number:00}", "A file is attached to the episode its own number names.");
        }

        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count, "Nothing is downloaded again for an episode the pack already delivered.");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await world.GetRequestAsync(request.Id)).Status, "An All scope keeps monitoring the series.");
    }

    [TestMethod]
    public async Task AMultiEpisodeFileCoversEachOfItsEpisodesAndNeitherIsDownloadedAgain()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, Tmdb());
        const string Double = "Severance.S01E01-E02.1080p.WEB-DL.x264-GROUP";
        world.Indexer.Publish(Double);
        var episodeIds = await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        Assert.AreEqual(Double, Assert.ContainsSingle(world.Sabnzbd.Grabs).NzbName);

        await world.CompleteDownloadAsync(request, Double, [1, 2, 3, 4, 5], $"{Double}.mkv");
        await world.WantedPassAsync();
        await world.WantedPassAsync();

        var placed = await PlacedEpisodesAsync(world, work.Id);
        Assert.HasCount(2, placed, "The file covers the episodes it holds and no other.");
        CollectionAssert.AreEquivalent(new[] { episodeIds[0], episodeIds[1] }, placed.Select(item => item.EpisodeId).ToArray());
        StringAssert.Contains(placed.Single(item => item.EpisodeId == episodeIds[0]).Name, "S01E01");
        StringAssert.Contains(placed.Single(item => item.EpisodeId == episodeIds[1]).Name, "S01E02");
        Assert.AreEqual(2, await world.Db.StoredFiles.CountAsync(), "Each episode owns its file, so coverage and upgrades stay per episode.");
        Assert.AreEqual(1, world.Sabnzbd.Grabs.Count, "The second episode is not searched: the file already covers it.");
        Assert.IsFalse(await world.Db.WantedItems.AnyAsync(item => item.WorkId == work.Id), "Nothing is wanted once both episodes are covered.");
    }

    [TestMethod]
    public async Task ADownloadWithoutAVideoIsRejectedAndTheNextReleaseReachesPlay()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, Tmdb());
        const string First = "Severance.S01E01.1080p.WEB-DL.x264-FIRST";
        const string Second = "Severance.S01E01.1080p.BluRay.x264-SECOND";
        world.Indexer.Publish(First);
        world.Indexer.Publish(Second);
        var episodeIds = await EpisodeIdsAsync(world);
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "custom", episodeIds: [episodeIds[0]]);
        var grabbed = world.Sabnzbd.Grabs.Single().NzbName;

        await world.CompleteDownloadAsync(request, grabbed, $"{grabbed}.nfo");
        await world.WantedPassAsync();
        await world.WantedPassAsync();

        var retried = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, retried.Status, retried.StatusMessage);
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count);
        var next = world.Sabnzbd.Grabs[1].NzbName;
        Assert.AreNotEqual(grabbed, next, "The release without a video is not tried again.");
        Assert.AreEqual(0, await world.Db.StoredFiles.CountAsync());

        await world.CompleteDownloadAsync(retried, next, $"{next}.mkv");
        await world.WantedPassAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await world.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync());
    }

    [TestMethod]
    public async Task AFailedEpisodeDownloadRetriesTheNextReleaseOnceAndNeverLoops()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, Tmdb());
        const string First = "Severance.S01E01.1080p.WEB-DL.x264-FIRST";
        const string Second = "Severance.S01E01.1080p.BluRay.x264-SECOND";
        world.Indexer.Publish(First);
        world.Indexer.Publish(Second);
        var episodeIds = await EpisodeIdsAsync(world);
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "custom", episodeIds: [episodeIds[0]]);
        var failed = world.Sabnzbd.Grabs.Single().NzbName;

        await world.Operations.MarkFailedAsync(request.OperationId!.Value, "Out of retention");
        await world.RestartAsync();
        await world.WantedPassAsync();
        await world.WantedPassAsync();

        var retried = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, retried.Status, retried.StatusMessage);
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count, "The failure is retried once with the next release, not repeated every pass.");
        Assert.AreNotEqual(failed, world.Sabnzbd.Grabs[1].NzbName, "The release that failed is not tried again.");

        await world.CompleteDownloadAsync(retried, world.Sabnzbd.Grabs[1].NzbName, $"{world.Sabnzbd.Grabs[1].NzbName}.mkv");
        await world.WantedPassAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await world.GetRequestAsync(request.Id)).Status);
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync());
    }

    [TestMethod]
    public async Task ACompletedEpisodeWaitsForAnOfflineLibraryRootAndImportsOnceItIsBackWithoutAnotherDownload()
    {
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, Tmdb());
        world.Indexer.Publish(Release(1));
        world.Indexer.Publish(Release(2));
        await EpisodeIdsAsync(world);
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        await ImportAsync(world, request.Id, 1);
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync(), "The root now holds media, so a missing folder means an unmounted share.");
        await world.CompleteDownloadAsync(await world.GetRequestAsync(request.Id), Release(2), $"{Release(2)}.mkv");
        Directory.Move(world.LibraryRoot, world.LibraryRoot + "-away");
        var rootId = await world.Db.LibraryRoots.AsNoTracking().Select(root => root.Id).SingleAsync();
        var probed = await new LibraryRootAvailabilityService(world.Db, world.Storage).CheckAsync(rootId, force: true, CancellationToken.None);
        Assert.IsFalse(probed!.IsAvailable, "The health poll sees the unmounted root.");

        await world.WantedPassAsync();

        var waiting = await world.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Importing, waiting.Status, waiting.StatusMessage);
        Assert.AreEqual(1, await world.Db.StoredFiles.CountAsync());
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count, "An offline root never makes the release look bad.");

        Directory.Move(world.LibraryRoot + "-away", world.LibraryRoot);
        await world.RestartAsync();
        await world.WantedPassAsync();

        Assert.AreEqual(2, await world.Db.StoredFiles.CountAsync());
        Assert.AreEqual(2, world.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task AnEpisodeTheProviderAnnouncesLaterJoinsTheStructureAndIsGrabbedWhenItAirs()
    {
        var tmdb = Tmdb(daysToThirdEpisode: 5);
        await using var world = await VideoRequestToPlayWorld.CreateAsync(MediaAcquisitionKind.Tv, tmdb);
        foreach (var episode in new[] { 1, 2, 3, 4 })
        {
            world.Indexer.Publish(Release(episode));
        }

        await EpisodeIdsAsync(world);
        var work = await world.Db.Works.AsNoTracking().SingleAsync();
        var request = await world.RequestAsync(SeveranceTmdb, "Severance", scope: "all");
        await ImportAsync(world, request.Id, 1);
        await ImportAsync(world, request.Id, 2);
        Assert.AreEqual(3, await world.Db.WorkEpisodes.CountAsync(x => x.WorkId == work.Id));

        var now = DateTime.UtcNow;
        tmdb.AddSeries(95396, "Severance", now.AddDays(-30), (1, [(1, now.AddDays(-30)), (2, now.AddDays(-23)), (3, now.AddDays(5)), (4, now.AddDays(12))]));
        Assert.IsTrue(await DiscoverPageFactory.Tmdb(world.Db, tmdb).SyncSeriesStructureAsync(work.Id, SeveranceTmdb, "en-US", CancellationToken.None), "The show is still airing.");
        Assert.AreEqual(4, await world.Db.WorkEpisodes.CountAsync(x => x.WorkId == work.Id), "The new episode is part of the structure.");

        world.Clock.Advance(TimeSpan.FromDays(6));
        await world.WantedPassAsync();
        await world.WantedPassAsync();
        Assert.AreEqual(Release(3), world.Sabnzbd.Grabs[2].NzbName);
        await ImportAsync(world, request.Id, 3);
        Assert.AreEqual(3, world.Sabnzbd.Grabs.Count, "The fourth episode has not aired, so nothing searches it.");

        world.Clock.Advance(TimeSpan.FromDays(7));
        await world.WantedPassAsync();
        await world.WantedPassAsync();
        Assert.AreEqual(Release(4), world.Sabnzbd.Grabs[3].NzbName, "The announced episode is acquired once it airs.");
    }
}
