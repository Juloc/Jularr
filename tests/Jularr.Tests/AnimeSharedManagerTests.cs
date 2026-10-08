using Jularr.Web.Features.Acquisition.Access;
using Microsoft.EntityFrameworkCore;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;
using Microsoft.Extensions.DependencyInjection;
using Jularr.Web.Features.Acquisition.Wanted;
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
        var start = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

        Assert.AreEqual(0, await environment.Scheduler.AdvanceAsync(new DateTimeOffset(start, TimeSpan.Zero), CancellationToken.None), "Right after startup the monitored anime are not searched yet.");
        Assert.AreEqual(1, await environment.Scheduler.AdvanceAsync(new DateTimeOffset(start.Add(AnimeAcquisitionScheduler.StartupDelay).AddSeconds(1), TimeSpan.Zero), CancellationToken.None));

        Assert.AreEqual(0, await environment.Scheduler.AdvanceAsync(new DateTimeOffset(start.AddMinutes(10), TimeSpan.Zero), CancellationToken.None), "The AniList list interval has not elapsed.");
        Assert.IsTrue(environment.Scheduler.RequestRun());
        Assert.AreEqual(1, await environment.Scheduler.AdvanceAsync(new DateTimeOffset(start.AddMinutes(10), TimeSpan.Zero), CancellationToken.None), "A search the owner asked for does not wait for the interval.");
        Assert.AreEqual(1, await environment.Scheduler.AdvanceAsync(new DateTimeOffset(start.AddMinutes(90), TimeSpan.Zero), CancellationToken.None), "The periodic step runs again once the interval elapsed.");
        Assert.AreEqual(0, await new AnimeWantedSource(environment.Scheduler).PrepareAsync(start.AddMinutes(200), CancellationToken.None), "The periodic step is no request that advanced, so the Wanted pass counts nothing for it.");
    }

    [TestMethod]
    public async Task AMonitoredAnimeIsSearchedByTheSharedWantedPassAndImportedIntoTheAnimeLibraryRoot()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        var start = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

        await environment.RunWantedPassAsync(start);
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
    public async Task AnIndexerOutageIsNotAFailedSearchSoItNeverRaisesTheBackoffAndIsRetriedSoon()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        var start = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        var searching = start.Add(AnimeAcquisitionScheduler.StartupDelay).AddSeconds(1);

        environment.Prowlarr.Failure = new HttpRequestException("indexer down");
        await environment.RunWantedPassAsync(start);
        await environment.RunWantedPassAsync(searching);

        var outage = AnimeRequestPayload.Of((await environment.AnimeRequestAsync())!);
        Assert.AreEqual(0, outage.Searches, "An outage is not a failed search: the search count, and so the back-off, stays where it was.");
        Assert.IsTrue(outage.NextSearchUtc is { } retry && Math.Abs((retry - DateTime.UtcNow - ReleaseRequestTracker.UnavailableRetry).TotalMinutes) < 5, "It is retried within the shared outage delay (the request stamps its retry with the real clock).");
        var queriesDuringOutage = environment.Prowlarr.Queries.Count;

        environment.Prowlarr.Failure = null;
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        await environment.RunWantedPassAsync(searching.AddMinutes(5));
        Assert.AreEqual(queriesDuringOutage, environment.Prowlarr.Queries.Count, "Nothing searches again before the retry delay, however often the pass runs.");
        Assert.AreEqual(0, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task ADownloadThatFailedOnTheClientsStorageIsNeitherBlocklistedNorCountedAndTheEpisodeIsSearchedAgainLater()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));
        var start = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        await environment.RunWantedPassAsync(start);
        await environment.RunWantedPassAsync(start.Add(AnimeAcquisitionScheduler.StartupDelay).AddSeconds(1));
        var operation = (await environment.Operations.GetAsync((await environment.AnimeRequestAsync())!.OperationId!.Value))!;

        environment.Sabnzbd.History = new SabnzbdHistorySnapshot([new SabnzbdHistoryJob(operation.ExternalId!, Best, "Failed", "anime", null, "Out of disk space on /downloads", SabnzbdFailureKind.Storage, DateTimeOffset.UtcNow)]);
        var projected = await SabnzbdOperationProjector.ApplyAsync(environment.Operations, await environment.Operations.ListActiveExternalAsync(SabnzbdClient.ProviderId), new SabnzbdQueueSnapshot(false, null, null, []), environment.Sabnzbd.History, DateTime.UtcNow, CancellationToken.None);
        var failure = projected.Failed.Single();
        await environment.RunWantedPassAsync(start.AddHours(2));

        Assert.AreEqual(SabnzbdFailureKind.Storage, failure.FailureKind);
        Assert.AreEqual(0, (await environment.Acquisitions.LoadAsync()).Blocklist.Count, "The release is not blocklisted.");
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "No other release is tried for a problem of the client's own storage.");
        var request = (await environment.AnimeRequestAsync())!;
        var payload = AnimeRequestPayload.Of(request);
        Assert.AreEqual(0, payload.Searches, "The failed grab is not counted as a search, so the back-off is untouched.");
        Assert.IsTrue(payload.TriedReleases is null or { Count: 0 }, "The release may be tried again once the client's storage is fixed.");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        Assert.IsTrue(payload.NextSearchUtc is not null && payload.Episodes is null, "The episode waits for the shared outage delay instead of staying grabbed.");
    }

    [TestMethod]
    public async Task ASearchThatAnsweredWithNothingStillCountsAsAFailedSearch()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        var start = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        await environment.RunWantedPassAsync(start);

        await environment.RunWantedPassAsync(start.Add(AnimeAcquisitionScheduler.StartupDelay).AddSeconds(1));

        var payload = AnimeRequestPayload.Of((await environment.AnimeRequestAsync())!);
        Assert.AreEqual(1, payload.Searches, "No release found is the failed search the back-off is meant for.");
        Assert.IsNotNull(payload.NextSearchUtc);
    }

    [TestMethod]
    public async Task ASearchTheOwnerAsksForCutsTheWaitOfTheSharedWantedPassShort()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        var waiting = environment.WantedTrigger.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.IsFalse(waiting.IsCompleted, "Nothing asked for a pass yet.");

        Assert.IsTrue(environment.Scheduler.RequestRun());

        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, environment.Scheduler.QueuedRequests, "The queued search is what the woken pass runs.");
    }

    [TestMethod]
    public async Task ASeasonWhereEveryEpisodeIsWantedIsSearchedAsTheSeasonAndItsPackCoversAllOfThem()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        environment.Db.StoredFiles.RemoveRange(await environment.Db.StoredFiles.ToListAsync());
        await environment.Db.SaveChangesAsync();
        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release("Frieren.S01.1080p.WEB-DL.AAC.H.264-GRP", "pack"));

        var request = await environment.SearchNowAsync();

        Assert.IsFalse(environment.Prowlarr.Queries.Any(query => query.Contains("E0", StringComparison.OrdinalIgnoreCase)), "No query names an episode: " + string.Join(" | ", environment.Prowlarr.Queries));
        Assert.IsTrue(environment.Prowlarr.Queries.Any(query => query.Contains("S01", StringComparison.OrdinalIgnoreCase)), "The season is asked for: " + string.Join(" | ", environment.Prowlarr.Queries));
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request!.Status, request.StatusMessage);
        Assert.AreEqual(2, AnimeRequestPayload.Of(request).Episodes!.Count, "One pack satisfies both wanted episodes.");
    }

    [TestMethod]
    public async Task ADownloadRecordsTheIndexerAndReleaseGroupOnItsOperation()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();

        environment.Prowlarr.Releases.Add(AnimeAcquisitionEnvironment.Release(Best, "g1080"));

        var request = await environment.SearchNowAsync();

        var operation = await environment.Operations.GetAsync(request!.OperationId!.Value);
        Assert.IsTrue(DownloadOperationDetails.TryParse(operation!.Details, out var details));
        Assert.AreEqual("Test indexer", details!.ReleaseSource);
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
