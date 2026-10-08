using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;

namespace Jularr.Tests;

[TestClass]
public sealed class SabnzbdAcquisitionTests
{
    [TestMethod]
    public async Task StartPersistsRelationThatSurvivesRestartWithoutPlainUrls()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var service = environment.NewAcquisitionService(environment.NewAcquisitionStore());

        var result = await service.StartAsync(Request(3, "a", "b"), CancellationToken.None);

        Assert.IsTrue(result.Submitted);
        var grab = environment.Client.Grabs.Single();
        Assert.AreEqual("anime", grab.Category);
        Assert.AreEqual("Show - 01 [a]", grab.NzbName);
        Assert.AreEqual(new Uri("https://indexer.example/a.nzb?apikey=indexer-secret"), grab.NzbUrl);

        // A new store instance reads the persisted relation, as after a restart.
        var restarted = await environment.NewAcquisitionStore().LoadAsync();
        var acquisition = restarted.Acquisitions.Single();
        Assert.AreEqual(result.AcquisitionId, acquisition.Id);
        Assert.AreEqual("show-key", acquisition.AnimeKey);
        Assert.AreEqual(new AnimeEpisodeKey("show-key", 1, 1), acquisition.Episodes.Single());
        Assert.AreEqual(result.OperationId, acquisition.LatestAttempt!.OperationId);
        Assert.AreEqual("release:a", acquisition.LatestAttempt.ReleaseIdentity);
        Assert.AreEqual("release:b", acquisition.PendingCandidates.Single().ReleaseIdentity);

        var operation = await new OperationStore(environment.Db).GetAsync(result.OperationId!.Value);
        Assert.AreEqual(SabnzbdAcquisitionService.OperationKind, operation!.Kind);
        StringAssert.Contains(operation.Subject, "Show");
        StringAssert.Contains(operation.Subject, "S01E01");

        var raw = await File.ReadAllTextAsync(
            Path.Combine(environment.Directory.FullName, SabnzbdAcquisitionStore.FileName));
        Assert.IsFalse(raw.Contains("indexer-secret", StringComparison.Ordinal));
        Assert.IsFalse(raw.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task FailedDownloadIsBlocklistedAndNextCandidateTriedWithinBound()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        var service = environment.NewAcquisitionService(store);
        var operations = new OperationStore(environment.Db);

        var first = await service.StartAsync(Request(2, "a", "b", "c"), CancellationToken.None);

        // Monitor path: SABnzbd history reports the job as failed.
        var failure = await FailInHistoryAsync(environment, operations, "Encrypted archive requires a password");
        var second = await service.HandleFailedAsync(
            failure.Operation.Id,
            failure.FailureKind,
            failure.Reason,
            CancellationToken.None);

        Assert.AreEqual(first.OperationId, failure.Operation.Id);
        Assert.IsTrue(second!.Submitted);
        Assert.AreEqual(2, environment.Client.Grabs.Count);
        StringAssert.Contains(environment.Client.Grabs[1].NzbName, "[b]");

        var state = await store.LoadAsync();
        var blocked = state.Blocklist.Single();
        Assert.AreEqual("release:a", blocked.ReleaseIdentity);
        Assert.AreEqual(SabnzbdFailureKind.Password, blocked.FailureKind);
        Assert.AreEqual(first.OperationId, blocked.OperationId);

        // Second failure reaches the bound of two attempts: no third grab.
        var secondFailure = await FailInHistoryAsync(environment, operations, "Repair failed, not enough repair blocks");
        var third = await service.HandleFailedAsync(
            secondFailure.Operation.Id,
            secondFailure.FailureKind,
            secondFailure.Reason,
            CancellationToken.None);

        Assert.IsFalse(third!.Submitted);
        Assert.AreEqual(2, environment.Client.Grabs.Count);
        StringAssert.Contains(third.Message, "2 of 2 attempts");
        Assert.AreEqual(2, (await store.LoadAsync()).Blocklist.Count);

        var logs = await operations.ListLogsAsync(
            new OperationLogFilter(OperationId: secondFailure.Operation.Id));
        Assert.IsTrue(logs.Any(entry => entry.Module == "Acquisition" && entry.Message.Contains("2 of 2")));
    }

    [TestMethod]
    public async Task BlocklistedReleasesAreSkippedAndRejectedSubmissionsAdvance()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        await store.BlockAsync(
            new SabnzbdBlockedRelease("release:a", "Show - 01 [a]", "show-key", SabnzbdFailureKind.Unpack, "old failure", null, DateTimeOffset.UtcNow));
        environment.Client.GrabResults.Enqueue(new SabnzbdGrabResult(false, [], "Invalid NZB"));
        var service = environment.NewAcquisitionService(store);

        var result = await service.StartAsync(Request(3, "a", "b", "c"), CancellationToken.None);

        Assert.IsTrue(result.Submitted);
        CollectionAssert.AreEqual(
            new[] { "Show - 01 [b]", "Show - 01 [c]" },
            environment.Client.Grabs.Select(grab => grab.NzbName).ToArray());

        var state = await store.LoadAsync();
        Assert.IsTrue(state.IsBlocked("release:b"));
        Assert.AreEqual(2, state.Acquisitions.Single().Attempts.Length);
        Assert.AreEqual(OperationStatus.Failed, (await new OperationStore(environment.Db)
            .GetAsync(state.Acquisitions.Single().Attempts[0].OperationId))!.Status);
    }

    [TestMethod]
    public async Task RecoveryAfterRestartAdvancesUnhandledFailureOnce()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var first = await environment
            .NewAcquisitionService(environment.NewAcquisitionStore())
            .StartAsync(Request(3, "a", "b"), CancellationToken.None);

        // The monitor recorded the failure but the process stopped before the
        // acquisition could continue.
        await new OperationStore(environment.Db).MarkFailedAsync(
            first.OperationId!.Value,
            "SABnzbd: extraction failed — Unpacking failed");

        var restarted = environment.NewAcquisitionService(environment.NewAcquisitionStore());
        Assert.AreEqual(1, await restarted.RecoverAsync(CancellationToken.None));
        Assert.AreEqual(0, await restarted.RecoverAsync(CancellationToken.None));

        Assert.AreEqual(2, environment.Client.Grabs.Count);
        var state = await environment.NewAcquisitionStore().LoadAsync();
        Assert.IsTrue(state.IsBlocked("release:a"));
        Assert.AreEqual(2, state.Acquisitions.Single().LatestAttempt!.Number);
    }

    [TestMethod]
    public async Task CancelStopsDownloadWithoutTryingNextCandidate()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        var service = environment.NewAcquisitionService(store);
        var started = await service.StartAsync(Request(3, "a", "b"), CancellationToken.None);
        var operations = new OperationStore(environment.Db);
        var nzoId = (await operations.GetAsync(started.OperationId!.Value))!.ExternalId;

        var cancelled = await environment.NewDownloadService(store)
            .CancelAsync(started.OperationId.Value, CancellationToken.None);

        Assert.IsTrue(cancelled.Success);
        Assert.AreEqual(nzoId, environment.Client.Cancelled.Single());
        Assert.AreEqual(OperationStatus.Cancelled, (await operations.GetAsync(started.OperationId.Value))!.Status);
        Assert.AreEqual(0, await service.RecoverAsync(CancellationToken.None));
        Assert.AreEqual(1, environment.Client.Grabs.Count);
        Assert.AreEqual(0, (await store.LoadAsync()).Blocklist.Count);
    }

    [TestMethod]
    public async Task CancelUsesTheConnectionThatAcceptedTheDownload()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        var started = await environment.NewAcquisitionService(store)
            .StartAsync(Request(1, "a"), CancellationToken.None);

        var clients = environment.NewDownloadClientStore();
        await clients.SaveAsync(
            new DownloadClientEntry(
                Guid.NewGuid(),
                "Higher priority SABnzbd",
                DownloadClientType.Sabnzbd,
                Enabled: true,
                Priority: 0,
                new DownloadClientSettings("http://higher-priority:8080", new Dictionary<MediaAcquisitionKind, string?> { [MediaAcquisitionKind.Book] = "books", [MediaAcquisitionKind.Anime] = "anime" }),
                "secret-key"));

        var cancelled = await environment.NewDownloadService(store)
            .CancelAsync(started.OperationId!.Value, CancellationToken.None);

        Assert.IsTrue(cancelled.Success);
        Assert.AreEqual("http://sabnzbd:8080", environment.Client.CancelConnections.Single().Settings.BaseUrl);
    }

    [TestMethod]
    public async Task AFailureOfTheClientsOwnStorageNeitherBlocklistsTheReleaseNorMovesOnToAnotherOne()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        var service = environment.NewAcquisitionService(store);
        var operations = new OperationStore(environment.Db);
        await service.StartAsync(Request(2, "a", "b"), CancellationToken.None);

        var failure = await FailInHistoryAsync(environment, operations, "Out of disk space on /downloads");
        var next = await service.HandleFailedAsync(failure.Operation.Id, failure.FailureKind, failure.Reason, CancellationToken.None);

        Assert.AreEqual(SabnzbdFailureKind.Storage, failure.FailureKind);
        Assert.IsNull(next, "The release was fine, so no other release is tried.");
        Assert.AreEqual(1, environment.Client.Grabs.Count);
        Assert.AreEqual(0, (await store.LoadAsync()).Blocklist.Count, "The release stays usable for the next search.");
    }

    [TestMethod]
    public async Task RetryRequeuesLatestFailedAttemptAndUnblocksRelease()
    {
        await using var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var store = environment.NewAcquisitionStore();
        var service = environment.NewAcquisitionService(store);
        var operations = new OperationStore(environment.Db);
        var started = await service.StartAsync(Request(2, "a", "b"), CancellationToken.None);

        var failure = await FailInHistoryAsync(environment, operations, "Unpacking failed");
        var replacement = await service.HandleFailedAsync(
            failure.Operation.Id, failure.FailureKind, failure.Reason, CancellationToken.None);
        var downloads = environment.NewDownloadService(store);

        // The first attempt was already replaced by the next candidate.
        var refused = await downloads.RetryAsync(started.OperationId!.Value, CancellationToken.None);
        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Message, "newer release");

        // The latest attempt fails too; the owner retries it manually.
        var lastFailure = await FailInHistoryAsync(environment, operations, "Unpacking failed");
        await service.HandleFailedAsync(
            lastFailure.Operation.Id, lastFailure.FailureKind, lastFailure.Reason, CancellationToken.None);
        Assert.IsTrue((await store.LoadAsync()).IsBlocked("release:b"));
        var previousNzo = lastFailure.Operation.ExternalId!;

        var retried = await downloads.RetryAsync(replacement!.OperationId!.Value, CancellationToken.None);

        Assert.IsTrue(retried.Success);
        Assert.AreEqual(previousNzo, environment.Client.Retried.Single());
        Assert.AreEqual("http://sabnzbd:8080", environment.Client.RetryConnections.Single().Settings.BaseUrl);
        var operation = await operations.GetAsync(replacement.OperationId.Value);
        Assert.AreEqual(OperationStatus.Running, operation!.Status);
        Assert.AreEqual(2, operation.Attempt);
        Assert.AreEqual($"{previousNzo}_retry", operation.ExternalId);
        Assert.IsFalse((await store.LoadAsync()).IsBlocked("release:b"));
        Assert.IsTrue((await store.LoadAsync()).IsBlocked("release:a"));
    }

    [TestMethod]
    public void AcceptedCandidatesComeFromScoredUsenetReleases()
    {
        var profile = AnimeQualityProfiles.CreateDefaultAnime1080p();
        var releases = new[]
        {
            Release("Show - 01 [720p]", "guid-720", 720, "usenet"),
            Release("Show - 01 [1080p]", "guid-1080", 1080, "usenet"),
            Release("Show - 01 [1080p torrent]", "guid-torrent", 1080, "torrent"),
            Release("Show - 01 [480p]", "guid-480", 480, "usenet")
        };

        var candidates = SabnzbdAcquisitionService.SelectAcceptedCandidates(releases, profile);

        CollectionAssert.AreEqual(
            new[] { "prowlarr:7:guid-1080", "prowlarr:7:guid-720" },
            candidates.Select(candidate => candidate.ReleaseIdentity).ToArray());
        Assert.AreEqual(new Uri("https://prowlarr.example/7/download?id=guid-1080"), candidates[0].NzbUrl);
    }

    private static SabnzbdAnimeAcquisitionRequest Request(int maxAttempts, params string[] releases) =>
        new(
            "show-key",
            "Show",
            [new AnimeEpisodeKey("show-key", 1, 1)],
            "owner",
            releases
                .Select(release => new SabnzbdAnimeReleaseCandidate(
                    $"release:{release}",
                    $"Show - 01 [{release}]",
                    new Uri($"https://indexer.example/{release}.nzb?apikey=indexer-secret")))
                .ToArray(),
            maxAttempts);

    private static async Task<SabnzbdProjectedFailure> FailInHistoryAsync(
        SabnzbdTestEnvironment environment,
        OperationStore operations,
        string failureMessage)
    {
        var active = await operations.ListActiveExternalAsync(SabnzbdClient.ProviderId);
        var job = active.Single();
        var result = await SabnzbdOperationProjector.ApplyAsync(
            operations,
            active,
            new SabnzbdQueueSnapshot(false, null, null, []),
            new SabnzbdHistorySnapshot(
            [
                new SabnzbdHistoryJob(
                    job.ExternalId!,
                    job.Subject ?? "job",
                    "Failed",
                    "anime",
                    null,
                    failureMessage,
                    SabnzbdClient.ClassifyFailure("Failed", failureMessage),
                    DateTimeOffset.UtcNow)
            ]),
            DateTime.UtcNow,
            CancellationToken.None);
        return result.Failed.Single();
    }

    private static AcquisitionCandidate Release(
        string title,
        string guid,
        int resolution,
        string protocol) =>
        new(
            title,
            "Indexer",
            7,
            protocol,
            1_000_000_000,
            Seeders: null,
            Leechers: null,
            PublishedAt: null,
            AgeDays: null,
            AgeHours: null,
            guid,
            InfoUrl: null,
            AnimeReleaseParser.Parse($"[Group] Show - 01 [WEB-DL {resolution}p]"),
            MatchedQueries: [],
            new Uri($"https://prowlarr.example/7/download?id={guid}"),
            InternalMagnetUri: null);
}
