using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeLegacyAcquisitionMigrationTests
{
    private const string Best = "Frieren.S01E02.1080p.WEB-DL.AAC.H.264-GRP";

    private static async Task<SabnzbdAcquisition> StartLegacyAsync(AnimeAcquisitionEnvironment environment, string release)
    {
        var outcome = await environment.WithScopeAsync(services => services.GetRequiredService<DownloadClientSubmissionService>().SubmitAsync(
            new DownloadSubmissionSpec("anime-sabnzbd-download", "Anime download · attempt 1 of 3", $"Frieren · S01E02 · {release}", null, new Uri("https://indexer.example/a.nzb"), release, MediaAcquisitionKind.Anime),
            CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        var acquisition = new SabnzbdAcquisition(
            Guid.NewGuid(),
            AnimeAcquisitionEnvironment.AnimeKey,
            "Frieren",
            [new AnimeEpisodeKey(AnimeAcquisitionEnvironment.AnimeKey, 1, 2, 2)],
            null,
            3,
            [new SabnzbdAcquisitionAttempt(1, outcome.OperationId, $"release:{release}", release, now)],
            [],
            now,
            now);
        await environment.Acquisitions.UpdateAsync(state => state.Acquisitions.Add(acquisition));
        await environment.Ownership.UpdateAsync(state => SonarrParallelSafety.RegisterJob(state, new AcquisitionOwnership(acquisition.Id.ToString(), AnimeAcquisitionEnvironment.AnimeKey, AcquisitionOwner.Jularr, "release-key", AcquisitionOwnershipStatus.Pending, now, outcome.ExternalId)));
        return acquisition;
    }

    [TestMethod]
    public async Task ADownloadTheOldPipelineHadInFlightBecomesTheDownloadOfTheRequestAndIsImportedByTheWantedPass()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        var acquisition = await StartLegacyAsync(environment, Best);
        var operationId = acquisition.LatestAttempt!.OperationId;

        await environment.Scheduler.RecoverAsync(CancellationToken.None);

        var request = (await environment.AnimeRequestAsync())!;
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        Assert.AreEqual(operationId, request.OperationId);
        Assert.AreEqual(2, AnimeRequestPayload.Of(request).Episodes!.Single().EpisodeNumber);
        Assert.AreEqual(0, (await environment.Acquisitions.LoadAsync()).Acquisitions.Count, "The relation is gone: the request owns the download now.");
        var jobs = (await environment.Ownership.LoadAsync()).Jobs;
        Assert.IsTrue(jobs.ContainsKey(operationId.ToString()) && !jobs.ContainsKey(acquisition.Id.ToString()), "The ownership job follows the Operation.");

        await environment.CompleteLatestDownloadAsync(environment.AddCompletedDownload(Best, $"{Best}.mkv"), acquisition);
        await environment.RequestPassAsync();
        Assert.IsNotNull(await environment.MediaFileAsync(1, 2), "The download was imported without being fetched again.");
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "Only the old pipeline's own submission: nothing was grabbed again.");
    }

    [TestMethod]
    public async Task AnAcquisitionThatIsNoLongerInFlightIsDroppedWithoutTouchingRequests()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        var acquisition = await StartLegacyAsync(environment, Best);
        await environment.Operations.MarkFailedAsync(acquisition.LatestAttempt!.OperationId, "SABnzbd download failed.");

        await environment.Scheduler.RecoverAsync(CancellationToken.None);

        Assert.AreEqual(0, (await environment.Acquisitions.LoadAsync()).Acquisitions.Count);
        Assert.IsNull(await environment.AnimeRequestAsync());
    }

    [TestMethod]
    public async Task AnAnimeWithoutAnAniListMatchHasNoRequestSoItsDownloadIsLeftToTheManualImport()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await StartLegacyAsync(environment, Best);
        await environment.Db.AnimeMetadata.ExecuteDeleteAsync();

        await environment.Scheduler.RecoverAsync(CancellationToken.None);

        Assert.AreEqual(0, (await environment.Acquisitions.LoadAsync()).Acquisitions.Count);
        Assert.IsNull(await environment.AnimeRequestAsync());
    }
}
