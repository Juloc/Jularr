using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// An anime request covers the episodes that have aired. It is Completed when every one of them is in the library, and the episodes
/// that air later are picked up by the series' monitoring without keeping the request open; an episode whose release is not known
/// is not part of the request yet.
/// </summary>
[TestClass]
public sealed class AnimeRequestAiredScopeTests
{
    private const string FrierenId = "154587";
    private const string Episode1 = "Frieren.S01E01.1080p.WEB-DL.AAC.H.264-GRP";

    private static (int Episode, DateTimeOffset At)[] Schedule(int aired, int total) =>
    [
        .. Enumerable.Range(1, total).Select(episode => (episode, DateTimeOffset.UtcNow.AddDays((episode - aired) * 7 - 1)))
    ];

    private static void AddEpisodeFile(AnimeAcquisitionEnvironment environment, int episode) =>
        environment.AddLibraryFile("Frieren", "Season 01", $"Frieren - S01E{episode:00} - Episode {episode}.mkv");

    [TestMethod]
    public async Task ASeriesThatIsAiringCompletesWhenItsAiredEpisodesAreInTheLibraryAndMonitoringKeepsTheRest()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 12, status: "RELEASING");
        await environment.SeedReleaseCalendarAsync(FrierenId, "RELEASING", Schedule(aired: 3, total: 12));

        var request = await environment.SubmitRequestAsync(FrierenId);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, "Episodes 2 and 3 have aired and are missing.");

        AddEpisodeFile(environment, 2);
        AddEpisodeFile(environment, 3);
        await environment.ScanAsync();
        await environment.RequestPassAsync();

        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.AreEqual(3, await environment.Db.MediaFiles.CountAsync());
        Assert.IsTrue((await environment.MonitoringStateAsync()).Anime[AnimeAcquisitionEnvironment.AnimeKey].Monitored, "Later episodes are still searched by the series' monitoring.");
    }

    [TestMethod]
    public async Task ARequestMadeLateInALongRunCoversTheOlderEpisodesTheCalendarNoLongerListsAsWell()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 24, status: "RELEASING");
        // The cached calendar only reaches back about a month: episodes 10 to 14 have aired, 15 on are upcoming, 1 to 9 are not listed any more.
        await environment.SeedReleaseCalendarAsync(FrierenId, "RELEASING", [.. Enumerable.Range(10, 15).Select(episode => (episode, DateTimeOffset.UtcNow.AddDays((episode - 14) * 7 - 1)))]);

        var request = await environment.SubmitRequestAsync(FrierenId);
        for (var episode = 10; episode <= 14; episode++)
        {
            AddEpisodeFile(environment, episode);
        }

        await environment.ScanAsync();
        await environment.RequestPassAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await environment.GetRequestAsync(request.Id)).Status, "Episodes 2 to 9 have aired too, they are only older than the calendar's window.");

        for (var episode = 2; episode <= 9; episode++)
        {
            AddEpisodeFile(environment, episode);
        }

        await environment.ScanAsync();
        await environment.RequestPassAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.GetRequestAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task AFinishedMultiCourSeriesNeedsEveryEpisodeOfBothEntriesEvenWhenOnlyTheCalendarKnowsTheSecondOneIsFinished()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 2, status: "FINISHED");
        await environment.AniListAccounts.TryAddEpisodeMappingAsync(new AnimeEpisodeMetadataMapping(Guid.NewGuid(), environment.AnimeId, 1, 1, 2, 1, "anilist", FrierenId, "Frieren", 2, DateTimeOffset.UtcNow), CancellationToken.None);
        await environment.AniListAccounts.TryAddEpisodeMappingAsync(new AnimeEpisodeMetadataMapping(Guid.NewGuid(), environment.AnimeId, 1, 3, 4, 1, "anilist", "9002", "Frieren Part Two", 2, DateTimeOffset.UtcNow), CancellationToken.None);
        await environment.SeedReleaseCalendarAsync("9002", "FINISHED");

        var request = await environment.SubmitRequestAsync(FrierenId);
        AddEpisodeFile(environment, 2);
        await environment.ScanAsync();
        await environment.RequestPassAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await environment.GetRequestAsync(request.Id)).Status, "Episodes 3 and 4 belong to a finished entry: they have aired.");

        AddEpisodeFile(environment, 3);
        AddEpisodeFile(environment, 4);
        await environment.ScanAsync();
        await environment.RequestPassAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.GetRequestAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task AReleasingSeriesNothingIsKnownAboutIsNotCompletedBeforeEveryTrackedEpisodeHasAFile()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 3, status: "RELEASING");

        var request = await environment.SubmitRequestAsync(FrierenId);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, "Episodes 2 and 3 may or may not have aired: the request is not available yet.");

        AddEpisodeFile(environment, 2);
        AddEpisodeFile(environment, 3);
        await environment.ScanAsync();
        await environment.RequestPassAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.GetRequestAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task ASeriesWithoutAnEpisodeCountCompletesWithTheEpisodesThePipelineTracksEvenIfTheCalendarListsMore()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: null, status: "RELEASING");
        await environment.SeedReleaseCalendarAsync(FrierenId, "RELEASING", Schedule(aired: 3, total: 6));

        var request = await environment.SubmitRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, "The pipeline only tracks episode 1; nothing is expected that it will never search.");
    }

    [TestMethod]
    public async Task ASeriesWithoutAnEpisodeCountAndNoEpisodesStaysOpenAsLookingForMedia()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: null);

        var request = await environment.SubmitRequestAsync(FrierenId);
        await environment.RequestPassAsync();

        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, "Nothing is tracked and nothing is available.");
        Assert.AreEqual("Looking for the requested episodes.", request.StatusMessage);
    }
    [TestMethod]
    public async Task EpisodesThatHaveNotAiredYetAreNotPartOfTheRequest()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 12, status: "RELEASING");
        await environment.SeedReleaseCalendarAsync(FrierenId, "RELEASING", Schedule(aired: 1, total: 12));

        var request = await environment.SubmitRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, "Only episode 1 has aired and it is in the library.");
    }

    [TestMethod]
    [DataRow("MOVIE")]
    [DataRow("OVA")]
    public async Task AMovieOrOvaCompletesWhenItsOneEpisodeIsImported(string format)
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: 1, status: "FINISHED", format: format);
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode1, "e1"));

        var request = await environment.SubmitRequestAsync(FrierenId);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.SearchOnAdd, CancellationToken.None);
        var folder = environment.AddCompletedDownload(Episode1, $"{Episode1}.mkv");
        var download = await environment.CompleteLatestDownloadAsync(folder);
        Assert.AreEqual(AnimeImportStatus.Imported, (await environment.ImportCompletedAsync(download, folder))!.Status);
        await environment.RequestPassAsync();

        request = await environment.GetRequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, request.StatusMessage);
        Assert.AreEqual($"/Library/Anime/{anime.Id}", request.ResultUrl);
    }

    [TestMethod]
    public async Task ASpecialInTheLibraryNeitherBlocksNorChangesWhenTheRequestIsCompleted()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(episodeCount: 1, status: "FINISHED");
        environment.AddLibraryFile("Frieren", "Season 00", "Frieren - S00E01 - Special.mkv");
        await environment.ScanAsync();

        var request = await environment.SubmitRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Completed, request.Status, "The regular episode and the special are in the library; a special that is missing is never expected.");
        Assert.AreEqual(2, await environment.Db.MediaFiles.CountAsync());
    }
}
