using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;
using DiscoverIndexModel = Jularr.Web.Pages.Discover.IndexModel;

namespace Jularr.Tests;

/// <summary>#393: adding an anime from Discover creates the series like Sonarr and starts the Usenet search.</summary>
[TestClass]
public sealed class AnimeAcquisitionRequestExecutorTests
{
    private const string FrierenId = "154587";
    private const string Episode1 = "Frieren.S01E01.1080p.WEB-DL.AAC.H.264-GRP";

    [TestMethod]
    public async Task AnimeNotInTheLibraryIsCreatedMonitoredSearchedAndImportedIntoOneEntry()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: 2);
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Episode1, "e1"));

        var execution = await environment.ExecuteAnimeRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Approved, execution.Status, "Adding the series is not having its media: the request is only completed by the import.");
        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        Assert.AreEqual(AnimeAcquisitionEnvironment.AnimeKey, anime.Key, "The key matches the series folder the importer creates.");
        Assert.AreEqual($"/Library/Anime/{anime.Id}", execution.ResultUrl);
        var match = await environment.Db.AnimeMetadata.AsNoTracking().SingleAsync(item => item.AnimeId == anime.Id);
        Assert.AreEqual(FrierenId, match.ExternalId);

        var library = await new LibraryMediaCardQuery(environment.Db).GetEntriesAsync("reader", [Jularr.Web.Features.MediaCore.WorkMediaType.Anime], CancellationToken.None);
        Assert.AreEqual($"/Library/Anime/{anime.Id}", Assert.ContainsSingle(library.Entries).Card.Href, "A requested anime is in the Library before any scan or file.");

        var settings = (await environment.MonitoringStateAsync()).Anime[anime.Key];
        Assert.IsTrue(settings.Monitored);
        Assert.IsTrue(settings.SearchOnAdd);
        Assert.AreEqual(environment.Root.Id, settings.TargetRootId);
        Assert.AreEqual(AnimeManagementMode.JularrManaged, SonarrParallelSafety.GetMode(await environment.Ownership.LoadAsync(), anime.Key));
        Assert.AreEqual(1, environment.Scheduler.QueuedRequests, "The search-on-add run is queued.");

        var run = await environment.Scheduler.RunNowAsync(anime.Key, AnimeSearchTrigger.SearchOnAdd, CancellationToken.None);
        Assert.AreEqual(1, run.Grabs, run.ToString());
        Assert.AreEqual(Episode1, environment.Sabnzbd.Grabs.Single().NzbName);

        var download = environment.AddCompletedDownload(Episode1, $"{Episode1}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);

        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        Assert.AreEqual(1, await environment.Db.Anime.CountAsync(), "The scan after the import finds the created entry instead of adding another.");
        var file = await environment.Db.MediaFiles.AsNoTracking().SingleAsync();
        StringAssert.StartsWith(file.Path, environment.SeriesFolder);
        Assert.IsTrue(await environment.Db.Episodes.AnyAsync(item => item.Id == file.EpisodeId && item.AnimeId == anime.Id));
    }

    [TestMethod]
    public async Task WithoutALibraryRootTheRequestFailsAndPointsToTheSetting()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: 2);
        await environment.Db.LibraryRoots.ExecuteUpdateAsync(setters => setters.SetProperty(root => root.IsEnabled, false));

        var execution = await environment.ExecuteAnimeRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Failed, execution.Status);
        StringAssert.Contains(execution.Message, "Admin → System");
        Assert.AreEqual(0, await environment.Db.Anime.CountAsync());
    }

    [TestMethod]
    public async Task UnknownAniListEntryFailsWithoutCreatingAnything()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();

        var execution = await environment.ExecuteAnimeRequestAsync("999999");

        Assert.AreEqual(AcquisitionRequestStatus.Failed, execution.Status);
        Assert.AreEqual(0, await environment.Db.Anime.CountAsync());
    }

    [TestMethod]
    public async Task AnimeInTheLibraryIsMonitoredAndSearchedInsteadOfDuplicated()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(status: "FINISHED");
        await environment.Scheduler.RunExclusiveAsync(
            (pipeline, token) => pipeline.UpdateAnimeSettingsAsync(environment.AnimeId, false, false, null, [], token),
            CancellationToken.None);

        var execution = await environment.ExecuteAnimeRequestAsync(FrierenId);

        Assert.AreEqual(AcquisitionRequestStatus.Approved, execution.Status, "S01E02 has aired (the series is finished) and is missing, so the request is not completed.");
        Assert.AreEqual(1, await environment.Db.Anime.CountAsync());
        Assert.IsTrue((await environment.MonitoringStateAsync()).Anime[AnimeAcquisitionEnvironment.AnimeKey].Monitored);
        Assert.AreEqual(1, environment.Scheduler.QueuedRequests);
    }

    [TestMethod]
    public async Task UnmatchedSeriesFolderOfTheSameNameIsMatchedAndKeepsItsSonarrDecision()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        environment.AniListMetadata.Add(FrierenId, "Frieren", episodeCount: 2);
        environment.AddLibraryFile("Frieren", "Season 01", "Frieren - S01E01 - Episode 1.mkv");
        await environment.ScanAsync();

        var execution = await environment.ExecuteAnimeRequestAsync(FrierenId);

        var anime = await environment.Db.Anime.AsNoTracking().SingleAsync();
        Assert.IsTrue(await environment.Db.AnimeMetadata.AnyAsync(item => item.AnimeId == anime.Id && item.ExternalId == FrierenId));
        Assert.AreEqual(AcquisitionRequestStatus.Approved, execution.Status, "A folder that was already on disk stays Sonarr's until the owner migrates it.");
        Assert.IsFalse((await environment.Ownership.LoadAsync()).Anime.ContainsKey(anime.Key));
    }

    [TestMethod]
    [DataRow("Frieren (2023)")]
    [DataRow("[Group] Sousou no Frieren")]
    [DataRow("Frieren.Beyond.Journeys.End")]
    public void SeriesFolderKeyIsTheKeyTheScannerGivesTheFolder(string folder)
    {
        var root = Path.Combine(Path.GetTempPath(), "anime");
        Assert.IsTrue(MediaPathParser.TryParse(root, Path.Combine(root, folder, "Season 01", "x - S01E01.mkv"), out var descriptor));
        Assert.AreEqual(descriptor.AnimeKey, MediaPathParser.AnimeKeyForSeriesFolder(folder));
    }
}
