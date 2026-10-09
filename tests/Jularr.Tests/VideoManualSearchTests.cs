using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Microsoft.AspNetCore.Http;

namespace Jularr.Tests;

/// <summary>
/// Admin Manual Search for Movie and TV requests: candidates come from the same indexers, parser and scorer as automatic acquisition,
/// carry score and rejection reasons, and a selected one is submitted through the same grab path exactly once.
/// </summary>
[TestClass]
public sealed class VideoManualSearchTests
{
    private const string Dune = "Dune.2021.1080p.WEB-DL.x264-GROUP";
    private const string DuneLowerQuality = "Dune.2021.720p.WEB-DL.x264-MID";
    private const string DuneRejectedQuality = "Dune.2021.480p.WEB-DL.x264-LOW";
    private const string OtherTitle = "Arrival.2016.1080p.WEB-DL.x264-GROUP";

    private static Task<VideoAcquisitionTestHost> MovieHostAsync() =>
        VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            Dune,
            DuneLowerQuality,
            moreReleases: [DuneRejectedQuality, OtherTitle]);

    private static Task<VideoAcquisitionTestHost> TvHostAsync() =>
        VideoAcquisitionTestHost.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E02.1080p.WEB-DL.x264-GROUP",
            "Severance.S01E03.1080p.WEB-DL.x264-WRONGEP",
            addEpisode: true,
            addSecondEpisode: true,
            moreReleases: ["Severance.S02E02.1080p.WEB-DL.x264-WRONGSEASON", "Severance.S01.1080p.WEB-DL.x264-PACK", "Severance.S01E02.480p.WEB-DL.x264-LOW"]);

    private static ManualSearchCandidate Candidate(ManualSearchResult result, string title) => result.Candidates.Single(candidate => candidate.Title == title);

    [TestMethod]
    public async Task MovieSearchExplainsEligibleWarnedAndRejectedCandidatesWithoutGrabbing()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();

        var result = await host.Get<VideoManualSearchService>().SearchAsync(request.Id, null, refresh: true, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Searched);
        Assert.AreEqual(4, result.Candidates.Count);
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count, "Searching never downloads anything.");

        var eligible = Candidate(result, Dune);
        Assert.AreEqual(ManualSearchVerdict.Eligible, eligible.Verdict);
        Assert.IsTrue(eligible.CanGrab);
        Assert.AreEqual(ManualSearchReleaseType.Movie, eligible.ReleaseType);
        Assert.AreEqual("WEB-1080p", eligible.Quality);
        Assert.AreEqual(ManualSearchReasonCode.MatchesTarget, eligible.Reasons[0].Code);
        Assert.IsNotNull(eligible.Score);
        Assert.AreEqual(1, eligible.Rank, "Rank 1 is the release automatic selection takes.");

        var lower = Candidate(result, DuneLowerQuality);
        Assert.AreEqual(ManualSearchVerdict.Warning, lower.Verdict);
        Assert.IsTrue(lower.CanGrab, "A warning does not stop the owner from choosing it.");
        Assert.IsTrue(lower.Reasons.Any(reason => reason.Code == ManualSearchReasonCode.LowerQuality));
        Assert.AreEqual(2, lower.Rank);

        var rejected = Candidate(result, DuneRejectedQuality);
        Assert.AreEqual(ManualSearchVerdict.Rejected, rejected.Verdict);
        Assert.IsFalse(rejected.CanGrab);
        Assert.IsNull(rejected.Rank, "A rejected release has no rank.");
        var profileReason = rejected.Reasons.Single(reason => reason.Code == ManualSearchReasonCode.ProfileRejected);
        StringAssert.Contains(profileReason.Detail, "not allowed", "The rejection carries the scorer's own explanation.");

        var wrongTitle = Candidate(result, OtherTitle);
        Assert.AreEqual(ManualSearchVerdict.Rejected, wrongTitle.Verdict);
        Assert.IsFalse(wrongTitle.CanGrab);
        Assert.AreEqual(ManualSearchReasonCode.WrongTitle, wrongTitle.Reasons[0].Code);
        Assert.IsNotNull(wrongTitle.Score, "A rejected identity still shows what the profile would score; the score never repairs the identity.");
    }

    [TestMethod]
    public async Task TvSearchSeparatesWrongSeasonWrongEpisodeAndSeasonPackForTheRequestedEpisode()
    {
        await using var host = await TvHostAsync();
        var request = await host.CreateApprovedAsync();

        var result = await host.Get<VideoManualSearchService>().SearchAsync(request.Id, host.SecondEpisodeId, refresh: true, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("S01E02", result.Target.Unit!.Label, "The requested episode wins over the first missing one.");
        Assert.AreEqual(2, result.Target.MissingUnits.Count);

        Assert.AreEqual(ManualSearchReasonCode.MatchesTarget, Candidate(result, "Severance.S01E02.1080p.WEB-DL.x264-GROUP").Reasons[0].Code);
        Assert.AreEqual(ManualSearchReasonCode.WrongEpisode, Candidate(result, "Severance.S01E03.1080p.WEB-DL.x264-WRONGEP").Reasons[0].Code);
        Assert.AreEqual(ManualSearchReasonCode.WrongSeason, Candidate(result, "Severance.S02E02.1080p.WEB-DL.x264-WRONGSEASON").Reasons[0].Code);

        var pack = Candidate(result, "Severance.S01.1080p.WEB-DL.x264-PACK");
        Assert.AreEqual(ManualSearchReleaseType.SeasonPack, pack.ReleaseType);
        Assert.AreEqual("S01", pack.ParsedUnit);
        Assert.AreEqual(ManualSearchReasonCode.ContainsTarget, pack.Reasons[0].Code);
        Assert.IsTrue(pack.CanGrab, "A complete season pack that contains the target can be selected.");

        Assert.IsFalse(Candidate(result, "Severance.S01E03.1080p.WEB-DL.x264-WRONGEP").CanGrab);
        Assert.IsFalse(Candidate(result, "Severance.S02E02.1080p.WEB-DL.x264-WRONGSEASON").CanGrab);
        Assert.IsFalse(Candidate(result, "Severance.S01E02.480p.WEB-DL.x264-LOW").CanGrab);
    }

    [TestMethod]
    public async Task ManualSearchAndAutomaticAcquisitionAgreeOnWhatCanBeGrabbed()
    {
        await using var automatic = await MovieHostAsync();
        var started = await automatic.StartAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, started.Status);
        Assert.AreEqual(Dune, automatic.Environment.Client.Grabs.Single().NzbName, "Automatic acquisition grabs the best eligible release.");

        await using var manual = await MovieHostAsync();
        var request = await manual.CreateApprovedAsync();
        var result = await manual.Get<VideoManualSearchService>().SearchAsync(request.Id, null, refresh: true, CancellationToken.None);

        var best = result!.Candidates.Where(candidate => candidate.CanGrab).OrderByDescending(candidate => candidate.Verdict == ManualSearchVerdict.Eligible).First();
        Assert.AreEqual(Dune, best.Title);
        Assert.AreEqual(Dune, result.Candidates.Single(candidate => candidate.Rank == 1).Title, "Rank 1 is the release the automatic search grabbed.");
        CollectionAssert.AreEqual(Enumerable.Range(1, result.Candidates.Count(candidate => candidate.Rank is not null)).Select(position => (int?)position).ToArray(), result.Candidates.Where(candidate => candidate.Rank is not null).Select(candidate => candidate.Rank).ToArray(), "The list is in rank order.");
    }

    [TestMethod]
    public async Task SelectingACandidateSubmitsThroughTheSharedGrabPathExactlyOnce()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var identity = Candidate((await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None))!, DuneLowerQuality).Identity;

        var first = await service.GrabAsync(request.Id, null, identity, CancellationToken.None);

        Assert.AreEqual(ManualGrabStatus.Submitted, first.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, first.Request.Status);
        Assert.IsNotNull(first.Request.OperationId);
        Assert.AreEqual(DuneLowerQuality, host.Environment.Client.Grabs.Single().NzbName, "The owner's choice is submitted, not the best-scored release.");
        Assert.AreEqual(MediaAcquisitionKind.Movie, host.OperationDetails(first.Request).MediaKind);
        CollectionAssert.Contains(VideoRequestPayload.Parse(first.Request.PayloadJson)!.TriedReleases!.ToArray(), identity);

        var second = await service.GrabAsync(request.Id, null, identity, CancellationToken.None);

        Assert.AreEqual(ManualGrabStatus.AlreadySubmitted, second.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "A double submit never creates a second download.");

        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(5));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "The scheduler does not grab again while the manual download runs.");
    }

    [TestMethod]
    public async Task ConcurrentSelectionsOfTheSameCandidateCreateOneDownload()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var identity = Candidate((await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None))!, Dune).Identity;

        var outcomes = await Task.WhenAll(
            service.GrabAsync(request.Id, null, identity, CancellationToken.None),
            service.GrabAsync(request.Id, null, identity, CancellationToken.None),
            service.GrabAsync(request.Id, null, identity, CancellationToken.None));

        Assert.AreEqual(1, outcomes.Count(outcome => outcome.Status == ManualGrabStatus.Submitted));
        Assert.AreEqual(2, outcomes.Count(outcome => outcome.Status == ManualGrabStatus.AlreadySubmitted));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task RejectedOrUnknownIdentitiesCanNeverBeSubmitted()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var result = (await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None))!;

        foreach (var title in new[] { OtherTitle, DuneRejectedQuality })
        {
            var outcome = await service.GrabAsync(request.Id, null, Candidate(result, title).Identity, CancellationToken.None);
            Assert.AreEqual(ManualGrabStatus.NotAvailable, outcome.Status, title);
        }

        var forged = await service.GrabAsync(request.Id, null, "prowlarr:1:not-a-release-of-this-search", CancellationToken.None);
        Assert.AreEqual(ManualGrabStatus.NotAvailable, forged.Status);
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task TvSelectionGrabsForTheChosenEpisodeAndRecordsItAsActive()
    {
        await using var host = await TvHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var result = (await service.SearchAsync(request.Id, host.SecondEpisodeId, refresh: true, CancellationToken.None))!;

        var outcome = await service.GrabAsync(request.Id, host.SecondEpisodeId, Candidate(result, "Severance.S01E02.1080p.WEB-DL.x264-GROUP").Identity, CancellationToken.None);

        Assert.AreEqual(ManualGrabStatus.Submitted, outcome.Status);
        var payload = VideoRequestPayload.Parse(outcome.Request.PayloadJson)!;
        Assert.AreEqual(host.SecondEpisodeId, payload.ActiveWorkEpisodeId);
        Assert.AreEqual(2, payload.ActiveEpisodeNumber);
        Assert.AreEqual($"work-episode:{host.SecondEpisodeId:D}", host.OperationDetails(outcome.Request).TargetKey);
    }

    [TestMethod]
    public async Task CancellingAManuallyGrabbedDownloadKeepsTheExplicitCancellationSemantics()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var identity = Candidate((await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None))!, Dune).Identity;
        var grabbed = await service.GrabAsync(request.Id, null, identity, CancellationToken.None);

        await host.Operations.MarkCancelledAsync(grabbed.Request.OperationId!.Value, "Cancelled by owner.");
        await host.ProcessAsync(DateTime.UtcNow);
        await host.ProcessAsync(DateTime.UtcNow.AddDays(2));

        Assert.AreEqual(AcquisitionRequestStatus.Failed, (await host.GetAsync(request.Id)).Status, "A cancelled download fails the request.");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "Nothing replaces the cancelled release automatically.");

        var again = await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None);
        Assert.IsTrue(Candidate(again!, Dune).IsTried);
        Assert.IsFalse(Candidate(again!, Dune).CanGrab, "The cancelled release stays marked as tried; the owner picks another one explicitly.");
        Assert.IsTrue(Candidate(again!, DuneLowerQuality).CanGrab);
    }

    [TestMethod]
    public async Task ARequestThatIsRejectedOrGrabbedDuringTheSearchIsNeverGrabbed()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var identity = Candidate((await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None))!, Dune).Identity;
        var store = host.Get<AcquisitionAccessStore>();

        host.Indexer.OnSearch = () => store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Rejected, "Rejected meanwhile.", null, null, "owner", CancellationToken.None);
        var rejected = await service.GrabAsync(request.Id, null, identity, CancellationToken.None);

        Assert.AreEqual(ManualGrabStatus.NotSearchable, rejected.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await host.GetAsync(request.Id)).Status, "A rejected request is not forced to Downloading.");
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);

        await store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Approved, null, null, null, "owner", CancellationToken.None);
        host.Indexer.OnSearch = () => store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Downloading, "Grabbed by the scheduler.", null, null, null, CancellationToken.None);
        var grabbed = await service.GrabAsync(request.Id, null, identity, CancellationToken.None);

        Assert.AreEqual(ManualGrabStatus.NotSearchable, grabbed.Status, "A request the scheduler already grabbed for is not grabbed twice.");
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task TheStatusClaimIsConditionalOnThePriorStatus()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var store = host.Get<AcquisitionAccessStore>();

        Assert.IsNull(await store.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Failed, AcquisitionRequestStatus.Pending], AcquisitionRequestStatus.Searching, null, null, CancellationToken.None));
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(request.Id)).Status);
        var claim = await store.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Failed], AcquisitionRequestStatus.Searching, "Claimed.", null, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, claim!.PreviousStatus, "The claim reports what the request was before.");
        Assert.IsNull(await store.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Failed], AcquisitionRequestStatus.Searching, null, null, CancellationToken.None), "Only one claimant wins.");
    }

    [TestMethod]
    public async Task AStaleEpisodeIsRefusedInsteadOfSearchingAnotherOne()
    {
        await using var host = await TvHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var gone = Guid.NewGuid();

        var result = await service.SearchAsync(request.Id, gone, refresh: true, CancellationToken.None);

        Assert.IsFalse(result!.Searched);
        Assert.IsTrue(result.TargetChanged);
        Assert.IsNull(result.Target.Unit, "No other episode is silently substituted.");
        Assert.AreEqual(2, result.Target.MissingUnits.Count);
        var outcome = await service.GrabAsync(request.Id, gone, "release:1:whatever", CancellationToken.None);
        Assert.AreEqual(ManualGrabStatus.TargetChanged, outcome.Status);
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task AGrabWhoseStatusUpdateFailsAfterSubmissionEndsInDownloadingNeverInSearching()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var identity = Candidate((await host.Get<VideoManualSearchService>().SearchAsync(request.Id, null, refresh: true, CancellationToken.None))!, Dune).Identity;
        var plainUser = new AcquisitionRequestService(
            host.Get<AcquisitionAccessStore>(),
            [],
            new CurrentAccountContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }),
            host.Get<IMediaCapabilityService>(),
            host.Get<AcquisitionRequestSettingsStore>(),
            host.Get<Jularr.Web.Features.Events.IJularrEventPublisher>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AcquisitionRequestService>.Instance);
        var service = new VideoManualSearchService(
            host.Get<VideoAcquisitionEngine>(),
            host.Get<AcquisitionAccessStore>(),
            plainUser,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoManualSearchService>.Instance);

        var outcome = await service.GrabAsync(request.Id, null, identity, CancellationToken.None);

        Assert.AreEqual(ManualGrabStatus.Unrecorded, outcome.Status);
        var stored = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, stored.Status, "The request follows the download that exists, never stays Searching.");
        Assert.IsNotNull(stored.OperationId);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        await host.ProcessAsync(DateTime.UtcNow.AddHours(3));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "The scheduler follows the linked download instead of grabbing again.");
    }

    [TestMethod]
    public async Task ASubmissionThatBlowsUpIsNeverHandedBackToTheScheduler()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var service = host.Get<VideoManualSearchService>();
        var identity = Candidate((await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None))!, Dune).Identity;
        host.Environment.Client.GrabException = new InvalidOperationException("connection reset");

        var outcome = await service.GrabAsync(request.Id, null, identity, CancellationToken.None);

        Assert.AreEqual(ManualGrabStatus.Unrecorded, outcome.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, (await host.GetAsync(request.Id)).Status, "The release may have reached the client, so the request waits for the owner.");
        await host.ProcessAsync(DateTime.UtcNow.AddHours(3));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "Nothing grabs again automatically.");
    }

    [TestMethod]
    public async Task ARequestLeftSearchingWithoutADownloadIsRecoveredByTheWantedPass()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var store = host.Get<AcquisitionAccessStore>();
        await store.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Approved], AcquisitionRequestStatus.Searching, null, null, CancellationToken.None);

        await host.ProcessAsync(DateTime.UtcNow);
        Assert.AreEqual(AcquisitionRequestStatus.Searching, (await host.GetAsync(request.Id)).Status, "A fresh claim is a running search and is left alone.");
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);

        await host.ProcessAsync(DateTime.UtcNow + Jularr.Web.Features.Acquisition.Wanted.WantedAcquisitionService.StaleSearchingAfter + TimeSpan.FromMinutes(1));

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await host.GetAsync(request.Id)).Status, "The lost search is taken back and finds a release.");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task ASchedulerPassThatReadApprovedCannotOverwriteAManualClaim()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var store = host.Get<AcquisitionAccessStore>();
        var scheduler = new AcquisitionRequestService(
            store,
            host.Get<IEnumerable<IAcquisitionRequestExecutor>>(),
            host.Get<CurrentAccountContext>(),
            host.Get<IMediaCapabilityService>(),
            host.Get<AcquisitionRequestSettingsStore>(),
            host.Get<Jularr.Web.Features.Events.IJularrEventPublisher>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AcquisitionRequestService>.Instance,
            new ClaimingModules(() => store.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Approved], AcquisitionRequestStatus.Searching, "Manual claim.", null, CancellationToken.None)));

        var result = await scheduler.ContinueAsync(request.Id, CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Searching, result.Status, "The manual claim stays; the scheduler does not run the executor on top of it.");
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
    }

    private sealed class ClaimingModules(Func<Task> claim) : IInstanceModuleService
    {
        public async Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            await claim();
            return InstanceModuleSettings.Default;
        }

        public Task<bool> IsEnabledAsync(InstanceModule module, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<InstanceModuleSettings> SetAsync(InstanceModule module, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstanceModuleSettings> SaveAsync(InstanceModuleSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task ARequestThatIsNotWaitingForAReleaseIsNotSearchedOrGrabbed()
    {
        await using var host = await MovieHostAsync();
        var request = await host.StartAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        var service = host.Get<VideoManualSearchService>();

        var result = await service.SearchAsync(request.Id, null, refresh: true, CancellationToken.None);

        Assert.IsFalse(result!.Searched);
        Assert.AreEqual(0, result.Candidates.Count);
        var outcome = await service.GrabAsync(request.Id, null, "release:1:whatever", CancellationToken.None);
        Assert.AreEqual(ManualGrabStatus.NotSearchable, outcome.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task ADisabledMediaModuleHidesManualSearchForItsRequests()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var disabled = new VideoManualSearchService(
            host.Get<VideoAcquisitionEngine>(),
            host.Get<AcquisitionAccessStore>(),
            host.Get<AcquisitionRequestService>(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoManualSearchService>.Instance,
            new FixedModules(InstanceModuleSettings.Default.With(InstanceModule.Movie, false)));

        Assert.IsNull(await disabled.LoadAsync(request.Id, null, CancellationToken.None));
        Assert.IsNull(await disabled.SearchAsync(request.Id, null, refresh: true, CancellationToken.None));
        Assert.AreEqual(ManualGrabStatus.NotFound, (await disabled.GrabAsync(request.Id, null, "release:1:x", CancellationToken.None)).Status);

        var enabled = new VideoManualSearchService(
            host.Get<VideoAcquisitionEngine>(),
            host.Get<AcquisitionAccessStore>(),
            host.Get<AcquisitionRequestService>(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoManualSearchService>.Instance,
            new FixedModules(InstanceModuleSettings.Default.With(InstanceModule.Tv, false)));
        Assert.IsNotNull(await enabled.LoadAsync(request.Id, null, CancellationToken.None), "Disabling TV does not hide Movie.");
    }

    [TestMethod]
    public async Task OnlyMovieAndTvRequestsOpenInThisSurface()
    {
        await using var host = await MovieHostAsync();
        var book = await host.Get<AcquisitionAccessStore>().CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "book-1", "A Book", null, null),
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);

        Assert.IsNull(await host.Get<VideoManualSearchService>().LoadAsync(book.Id, null, CancellationToken.None));
        Assert.IsNull(await host.Get<VideoManualSearchService>().LoadAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    [TestMethod]
    public async Task ApplyingAManualGrabRequiresARequestManager()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();
        var plainUser = new AcquisitionRequestService(
            host.Get<AcquisitionAccessStore>(),
            [],
            new CurrentAccountContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }),
            host.Get<IMediaCapabilityService>(),
            host.Get<AcquisitionRequestSettingsStore>(),
            host.Get<Jularr.Web.Features.Events.IJularrEventPublisher>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AcquisitionRequestService>.Instance);

        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(
            () => plainUser.ApplyManualExecutionAsync(request.Id, new AcquisitionExecution(AcquisitionRequestStatus.Downloading, "x"), CancellationToken.None));
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await host.GetAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task CandidatesNeverCarryADownloadUrl()
    {
        await using var host = await MovieHostAsync();
        var request = await host.CreateApprovedAsync();

        var result = await host.Get<VideoManualSearchService>().SearchAsync(request.Id, null, refresh: true, CancellationToken.None);

        Assert.IsTrue(result!.Candidates.Count > 0);
        Assert.IsFalse(result.Candidates.Any(candidate => candidate.Identity.Contains("indexer.invalid/download", StringComparison.Ordinal)), "Candidates never carry a download URL.");
    }

    private sealed class FixedModules(InstanceModuleSettings settings) : IInstanceModuleService
    {
        public Task<InstanceModuleSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task<bool> IsEnabledAsync(InstanceModule module, CancellationToken cancellationToken = default) => Task.FromResult(settings.IsEnabled(module));

        public Task<InstanceModuleSettings> SetAsync(InstanceModule module, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstanceModuleSettings> SaveAsync(InstanceModuleSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
