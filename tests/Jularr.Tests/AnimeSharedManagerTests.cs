using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;

namespace Jularr.Tests;

/// <summary>Anime follows the shared manager: the Wanted pass decides when it runs, the LibraryRoot decides where and how it imports, and its downloads record where the release came from.</summary>
[TestClass]
public sealed class AnimeSharedManagerTests
{
    private const string Best = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    [TestMethod]
    public async Task TheWantedPassRunsAnimeOnItsCadenceAndQueuedSearchesAtOnce()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        var source = new AnimeWantedSource(environment.Scheduler);
        var start = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

        Assert.AreEqual(0, await source.PrepareAsync(start, CancellationToken.None), "Right after startup the monitored anime are not searched yet.");
        Assert.IsNull(environment.Scheduler.LastRun);
        Assert.AreEqual(1, await source.PrepareAsync(start.Add(AnimeAcquisitionScheduler.StartupDelay).AddSeconds(1), CancellationToken.None));
        Assert.IsNotNull(environment.Scheduler.LastRun);

        Assert.AreEqual(0, await source.PrepareAsync(start.AddMinutes(10), CancellationToken.None), "The canonical interval has not elapsed.");
        Assert.IsTrue(environment.Scheduler.RequestRun());
        Assert.AreEqual(1, await source.PrepareAsync(start.AddMinutes(10), CancellationToken.None), "A search the owner asked for does not wait for the interval.");
        Assert.AreEqual(1, await source.PrepareAsync(start.AddMinutes(90), CancellationToken.None), "The periodic search runs again once the interval elapsed.");
    }

    [TestMethod]
    public async Task AMonitoredAnimeIsSearchedByTheSharedWantedPassAndImportedIntoTheAnimeLibraryRoot()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        var start = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

        await environment.RunWantedPassAsync(start);
        Assert.AreEqual(0, environment.Sabnzbd.Grabs.Count, "The first pass after startup only recovers.");
        await environment.RunWantedPassAsync(start.Add(AnimeAcquisitionScheduler.StartupDelay).AddSeconds(1));

        Assert.AreEqual(Best, environment.Sabnzbd.Grabs.Single().NzbName);
        var download = environment.AddCompletedDownload(Best, $"{Best}.mkv");
        var record = await environment.ImportCompletedAsync(await environment.CompleteLatestDownloadAsync(download), download);
        Assert.AreEqual(AnimeImportStatus.Imported, record!.Status, record.Message);
        var imported = await environment.MediaFileAsync(1, 2);
        Assert.IsTrue(imported!.Path.StartsWith(environment.Root.Path, StringComparison.Ordinal), "The episode lands in the Anime LibraryRoot.");
        Assert.IsFalse(File.Exists(Path.Combine(download, $"{Best}.mkv")), "The root's placement policy (Move) moved the file.");
    }

    [TestMethod]
    public async Task ADownloadRecordsTheIndexerAndReleaseGroupOnItsOperation()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();

        var result = await environment.StartAcquisitionAsync([new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)], Best, "Indexer A", "GRP");

        var operation = await environment.Operations.GetAsync(result.OperationId!.Value);
        Assert.IsTrue(DownloadOperationDetails.TryParse(operation!.Details, out var details));
        Assert.AreEqual("Indexer A", details!.ReleaseSource);
        Assert.AreEqual("GRP", details.ReleaseGroup);
    }

    [TestMethod]
    public async Task ANewAnimeGoesToTheAnimeDefaultRootAndIsPlacedByThePolicyOfThatRoot()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        var newAnime = Guid.NewGuid();

        var only = await environment.GetLibraryLocationAsync(newAnime);
        Assert.AreEqual(environment.Root.Id, only!.RootId, "With one Anime root there is nothing to choose.");
        Assert.AreEqual(ImportMode.Move, only.Mode);

        var second = new LibraryRoot { Name = "Anime 2", Path = Path.Combine(environment.TempRoot, "library-2"), PlacementPolicy = LibraryPlacementPolicy.Copy };
        environment.Db.LibraryRoots.Add(second);
        environment.Db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = second.Id, ContentType = LibraryContentType.Anime });
        await environment.Db.SaveChangesAsync();
        Assert.IsNull(await environment.GetLibraryLocationAsync(newAnime), "Two Anime roots and no default: the import waits for the owner to choose in Storage.");

        await new LibraryRootRoutingService(environment.Db).SetDefaultAsync(LibraryContentType.Anime, second.Id);
        var routed = await environment.GetLibraryLocationAsync(newAnime);
        Assert.AreEqual(second.Id, routed!.RootId);
        Assert.AreEqual(ImportMode.Copy, routed.Mode, "The placement comes from the root.");
        Assert.AreEqual(environment.Root.Id, (await environment.GetLibraryLocationAsync(newAnime, environment.Root.Id))!.RootId, "A root assigned to the anime itself wins over the default.");
    }
}
