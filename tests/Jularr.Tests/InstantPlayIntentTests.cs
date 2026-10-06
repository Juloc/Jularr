using System.Security.Claims;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The playback intent of Instant Play over the real request service, executor, Wanted pass and fake SABnzbd: a card click never
/// acquires, an explicit intent acquires the smallest unit through the one canonical request, never duplicates or approves around
/// one, and prioritizes what a profile is waiting for.
/// </summary>
[TestClass]
public sealed class InstantPlayIntentTests
{
    private const string Dune = "Dune.2021.1080p.WEB-DL.x264-GROUP";
    private const string Episode1 = "Severance.S01E01.1080p.WEB-DL.x264-GROUP";
    private const string Episode2 = "Severance.S01E02.1080p.WEB-DL.x264-GROUP";
    private const string Episode3 = "Severance.S01E03.1080p.WEB-DL.x264-GROUP";

    private static Task<VideoAcquisitionTestHost> MovieHostAsync(params string[] moreReleases) =>
        VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", Dune, moreReleases: moreReleases);

    private static async Task<VideoAcquisitionTestHost> SeriesHostAsync(int episodes = 3)
    {
        var host = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Tv, "Severance", 2022, "95396", Episode1, Episode2, addEpisode: true, addSecondEpisode: true, moreReleases: [Episode3]);
        if (episodes > 2)
        {
            await host.AddEpisodeAsync(1, 3);
        }

        return host;
    }

    private static string Directory(VideoAcquisitionTestHost host) => host.Environment.Directory.FullName;

    /// <summary>The intent service of one signed-in profile over the host's database, executors and Wanted pass.</summary>
    private static PlaybackIntentService Intents(VideoAcquisitionTestHost host, string profile = "owner", AccountRole role = AccountRole.Owner, Func<int, Task>? afterCapabilityRead = null, bool playback = true, TimeProvider? clock = null)
    {
        var account = AcquisitionAccessFixture.Account(profile, role);
        IMediaCapabilityService capabilities = new MediaCapabilityService(new MediaCapabilityStore(Directory(host)));
        if (afterCapabilityRead is not null)
        {
            capabilities = new HookedCapabilityService(capabilities, afterCapabilityRead);
        }

        var modules = new InstanceModuleStore(Directory(host));
        modules.SetAsync(InstanceModule.Playback, playback).GetAwaiter().GetResult();
        var db = host.Environment.Db;
        var store = host.Get<AcquisitionAccessStore>();
        var settings = new AcquisitionRequestSettingsStore(Directory(host));
        var executors = host.Services.GetServices<IAcquisitionRequestExecutor>();
        var requests = new AcquisitionRequestService(store, executors, account, capabilities, settings, new RecordingEventPublisher(), NullLogger<AcquisitionRequestService>.Instance, modules);
        var policies = new InstantPlayPolicyService(modules, requests, host.Get<VideoAcquisitionEngine>());
        var facts = new VideoPlaybackFactsQuery(db, store, new VideoProgressService(db), TimeProvider.System);
        return new PlaybackIntentService(facts, policies, requests, store, new ConsumerAcquisitionQuery(db, store, host.Get<VideoRequestWorkResolver>(), TimeProvider.System), account, clock ?? TimeProvider.System);
    }

    private static async Task<IReadOnlyList<AcquisitionRequest>> AllRequestsAsync(VideoAcquisitionTestHost host) =>
        await host.Requests.ListAllAsync(100, CancellationToken.None);

    private static PlaybackMarker Marker(Guid? episodeId, string profile = "owner", DateTime? at = null) => new(episodeId, profile, at ?? DateTime.UtcNow);

    private static Guid?[] MarkedUnits(VideoRequestPayload payload) => [.. payload.ActivePlaybackMarkers(DateTime.UtcNow).Select(marker => marker.WorkEpisodeId)];

    private static bool HasMarker(VideoRequestPayload payload) => payload.HasPlaybackIntent(DateTime.UtcNow);

    private static VideoRequestPayload PayloadOf(AcquisitionRequest request) => VideoRequestPayload.Parse(request.PayloadJson)!;

    // ---- Local media and permissions ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task ALocalTargetPlaysAndNeverCreatesARequest()
    {
        await using var host = await MovieHostAsync();
        await host.AttachFileAsync(null);

        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.PlayNow, result.Outcome);
        Assert.IsEmpty(await AllRequestsAsync(host));
    }

    [TestMethod]
    public async Task WithoutAutoApprovalTheIntentSaysRequestAndChangesNothing()
    {
        await using var host = await MovieHostAsync();

        var result = await Intents(host, "bob", AccountRole.User).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.RequestRequired, result.Outcome);
        Assert.AreEqual(PrimaryActionKind.Request, result.Action!.Kind);
        Assert.IsEmpty(await AllRequestsAsync(host));
        Assert.IsEmpty(host.Environment.Client.Grabs);
    }

    [TestMethod]
    public async Task AProfileWithInstantCapabilityAcquiresWithoutBeingOwner()
    {
        await using var host = await MovieHostAsync();
        await new MediaCapabilityStore(Directory(host)).SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Instant);

        var result = await Intents(host, "bob", AccountRole.User).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, result.Outcome);
        var request = Assert.ContainsSingle(await AllRequestsAsync(host));
        Assert.AreEqual(("bob", AcquisitionRequestStatus.Downloading), (request.RequestedByProfileId, request.Status));
    }

    [TestMethod]
    public async Task AManagerOnlyInstanceHasNoPlayIntentAndRequestsMissingMedia()
    {
        await using var host = await MovieHostAsync();
        await host.AttachFileAsync(null);
        var other = await host.AddWorkAsync("Arrival", "329865", 2016);

        var local = await Intents(host, playback: false).StartAsync(host.Work.Id, null, CancellationToken.None);
        var missing = await Intents(host, playback: false).StartAsync(other.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.NotAvailable, local.Outcome, "A manager-only instance has no play intent for local media.");
        Assert.AreEqual(PlaybackIntentOutcome.RequestRequired, missing.Outcome, "Missing media is requested, never watched now.");
        Assert.IsEmpty(await AllRequestsAsync(host));
    }

    [TestMethod]
    public async Task ASeriesTargetMustExistAndBelongToTheWork()
    {
        await using var host = await SeriesHostAsync();
        var other = await host.AddWorkAsync("Other Show", "777");
        var foreign = new WorkEpisode { WorkId = other.Id, SeasonNumber = 1, EpisodeNumber = 1 };
        host.Environment.Db.WorkEpisodes.Add(foreign);
        await host.Environment.Db.SaveChangesAsync();
        var intents = Intents(host);

        foreach (var (workId, episodeId) in new (Guid, Guid?)[] { (Guid.NewGuid(), null), (host.Work.Id, Guid.NewGuid()), (host.Work.Id, foreign.Id) })
        {
            Assert.AreEqual(PlaybackIntentOutcome.TargetNotFound, (await intents.StartAsync(workId, episodeId, CancellationToken.None)).Outcome);
        }

        Assert.IsEmpty(await AllRequestsAsync(host));
    }

    [TestMethod]
    public async Task AMovieHasNoEpisodeAndAnimeIsNotAVideoWork()
    {
        await using var host = await MovieHostAsync();
        var anime = new Work { MediaType = WorkMediaType.Anime, CanonicalTitle = "Starfall", Year = 2020 };
        host.Environment.Db.Works.Add(anime);
        await host.Environment.Db.SaveChangesAsync();
        var intents = Intents(host);

        Assert.AreEqual(PlaybackIntentOutcome.TargetNotFound, (await intents.StartAsync(host.Work.Id, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.AreEqual(PlaybackIntentOutcome.TargetNotFound, (await intents.StartAsync(anime.Id, null, CancellationToken.None)).Outcome);
        Assert.IsEmpty(await AllRequestsAsync(host));
    }


    // ---- The smallest unit, through the one request, with priority ---------------------------------------------------

    [TestMethod]
    public async Task AMovieIntentCreatesTheWholeMovieRequestAndPrioritizesItsDownload()
    {
        await using var host = await MovieHostAsync();

        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, result.Outcome);
        Assert.AreEqual(PrimaryActionKind.WatchNow, result.Action!.Kind);
        var request = Assert.ContainsSingle(await AllRequestsAsync(host));
        Assert.AreEqual(request.Id, result.RequestId);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, "Auto-approved by policy and executed through the shared pipeline.");
        Assert.AreEqual("owner", request.DecidedByProfileId, "Auto-approved by the profile's own capability, not around an approval.");
        var payload = PayloadOf(request);
        Assert.AreEqual((VideoRequestScope.WholeWork, false, true), (payload.Scope, payload.MonitorFuture, MarkedUnits(payload).Contains(null)));
        Assert.AreEqual(ConsumerAcquisitionState.GettingMedia, result.Acquisition!.State);
        Assert.AreEqual(ConsumerMediaUnit.Movie, result.Acquisition.MediaUnit);

        Assert.AreEqual(1, Assert.ContainsSingle(host.Environment.Client.Grabs).Priority, "SABnzbd takes the release it is waited for first (priority High).");
        Assert.AreEqual(OperationPriority.High, (await host.Operations.GetAsync(request.OperationId!.Value))!.Priority);
    }

    [TestMethod]
    public async Task ASeriesIntentRequestsOnlyTheNextRequiredEpisodeNeverTheWholeSeries()
    {
        await using var host = await SeriesHostAsync();
        var intents = Intents(host);

        var result = await intents.StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.Acquiring, PrimaryActionKind.StartWatching, host.EpisodeId), (result.Outcome, result.Action!.Kind, result.Action.WorkEpisodeId), "No history: the first episode.");
        var request = Assert.ContainsSingle(await AllRequestsAsync(host));
        var payload = PayloadOf(request);
        Assert.AreEqual(VideoRequestScope.Custom, payload.Scope);
        CollectionAssert.AreEqual(new[] { host.EpisodeId!.Value }, payload.SelectedEpisodeIds, "Exactly the target episode is in the scope.");
        Assert.IsFalse(payload.MonitorFuture, "No future monitoring from a playback intent.");
        CollectionAssert.AreEqual(new Guid?[] { host.EpisodeId.Value }, MarkedUnits(payload));
        Assert.AreEqual(ConsumerMediaUnit.Episode, result.Acquisition!.MediaUnit);
        Assert.AreEqual(1, Assert.ContainsSingle(host.Environment.Client.Grabs).Priority);

        // Finish the episode: the next intent after completed-through-1 requires episode 2 and only episode 2.
        host.CompleteInSabnzbd(request, "/downloads/tv/Severance.S01E01");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);
        Assert.IsTrue(await host.HasPlayableAsync(host.EpisodeId));
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await host.GetAsync(request.Id)).Status, "A request of one episode ends with it.");
        await new LibraryCanonicalSeed(host.Environment.Db).SetProgressAsync("owner", host.Work, await host.Environment.Db.WorkEpisodes.SingleAsync(x => x.Id == host.EpisodeId), 0, 1_500_000, completed: true, DateTime.UtcNow);

        var next = await intents.StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.Acquiring, host.SecondEpisodeId), (next.Outcome, next.Action!.WorkEpisodeId));
        var second = (await AllRequestsAsync(host)).Single(x => x.IsOpen);
        CollectionAssert.AreEqual(new[] { host.SecondEpisodeId!.Value }, PayloadOf(second).SelectedEpisodeIds);
    }

    [TestMethod]
    public async Task AnExplicitEpisodeIsTheTarget()
    {
        await using var host = await SeriesHostAsync();

        var result = await Intents(host).StartAsync(host.Work.Id, host.SecondEpisodeId, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.Acquiring, PrimaryActionKind.WatchNow), (result.Outcome, result.Action!.Kind));
        CollectionAssert.AreEqual(new[] { host.SecondEpisodeId!.Value }, PayloadOf(Assert.ContainsSingle(await AllRequestsAsync(host))).SelectedEpisodeIds);
        StringAssert.Contains(host.Environment.Client.Grabs.Single().NzbName!, "S01E02");
    }

    // ---- Existing requests: attach, never duplicate, never bypass approval ------------------------------------------

    [TestMethod]
    public async Task AnEquivalentActiveRequestIsReusedAndNothingIsDuplicated()
    {
        await using var host = await SeriesHostAsync();
        var existing = await host.StartAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, existing.Status);
        var grabs = host.Environment.Client.Grabs.Count;

        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.Acquiring, existing.Id), (result.Outcome, result.RequestId));
        Assert.AreEqual(1, (await AllRequestsAsync(host)).Count);
        Assert.AreEqual(grabs, host.Environment.Client.Grabs.Count, "No second download.");
        Assert.AreEqual(existing.OperationId, (await host.GetAsync(existing.Id)).OperationId);
    }

    [TestMethod]
    public async Task AnApprovedRequestThatDoesNotCoverTheTargetTakesItWithoutWideningItsScope()
    {
        await using var host = await SeriesHostAsync();
        var scope = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.Custom, [host.SecondEpisodeId!.Value], MonitorFuture: false);
        var existing = await host.CreateApprovedAsync(scope with { NextSearchUtc = DateTime.UtcNow.AddHours(6), Searches = 1 });

        var result = await Intents(host).StartAsync(host.Work.Id, host.EpisodeId, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.Acquiring, existing.Id), (result.Outcome, result.RequestId));
        Assert.AreEqual(1, (await AllRequestsAsync(host)).Count);
        var after = await host.GetAsync(existing.Id);
        var payload = PayloadOf(after);
        CollectionAssert.AreEqual(new[] { host.SecondEpisodeId.Value }, payload.SelectedEpisodeIds, "The saved scope is unchanged.");
        Assert.AreEqual(VideoRequestScope.Custom, payload.Scope);
        CollectionAssert.AreEqual(new Guid?[] { host.EpisodeId!.Value }, MarkedUnits(payload));
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, after.Status, "An idle request searches for the unit now, ahead of its back-off.");
        StringAssert.Contains(host.Environment.Client.Grabs.Single().NzbName!, "S01E01", "The playback episode is taken before the scoped one.");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Single().Priority);
    }

    [TestMethod]
    public async Task ARequestThatWaitsForApprovalIsShownAndNeverApprovedAround()
    {
        await using var host = await MovieHostAsync();
        var pending = await host.Requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", host.TmdbId, "Dune", null, null), "bob", AcquisitionRequestStatus.Pending, null, CancellationToken.None);

        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.AwaitingApproval, pending.Id), (result.Outcome, result.RequestId));
        Assert.AreEqual(ConsumerAcquisitionState.WaitingForApproval, result.Acquisition!.State);
        var after = await host.GetAsync(pending.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Pending, after.Status);
        Assert.IsNull(after.PayloadJson, "A pending request is not touched.");
        Assert.IsEmpty(host.Environment.Client.Grabs);
    }

    [TestMethod]
    public async Task ADoubleSubmitIsOneRequestAndOneDownload()
    {
        await using var host = await MovieHostAsync();
        var intents = Intents(host);

        var first = await intents.StartAsync(host.Work.Id, null, CancellationToken.None);
        var second = await intents.StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(first.RequestId, second.RequestId);
        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, second.Outcome);
        Assert.AreEqual(1, (await AllRequestsAsync(host)).Count);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task ARaceWithASecondTabCreatesOneRequestAndAttachesTheLoser()
    {
        await using var host = await MovieHostAsync();
        PlaybackIntentResult? winner = null;
        var calls = 0;
        // The first intent has read "no open request" and is about to submit when the second one runs to the end.
        var first = Intents(host, afterCapabilityRead: async _ =>
        {
            if (++calls == 2)
            {
                winner = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);
            }
        });

        var loser = await first.StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, winner!.Outcome);
        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, loser.Outcome);
        Assert.AreEqual(winner.RequestId, loser.RequestId);
        Assert.AreEqual(1, (await AllRequestsAsync(host)).Count, "The unique open-title index keeps one request.");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task ACoveredButUnprioritizedEpisodeIsPrioritizedWithoutTouchingTheRequest()
    {
        await using var host = await SeriesHostAsync();
        var existing = await host.StartAsync();
        var grabs = host.Environment.Client.Grabs.Count;
        var intents = Intents(host);

        var first = await intents.StartAsync(host.Work.Id, host.SecondEpisodeId, CancellationToken.None);
        var payload = PayloadOf(await host.GetAsync(existing.Id));
        var second = await intents.StartAsync(host.Work.Id, host.SecondEpisodeId, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.Acquiring, existing.Id), (first.Outcome, first.RequestId));
        Assert.AreEqual(VideoRequestScope.AllCurrentAndFuture, payload.Scope, "The scope is untouched.");
        CollectionAssert.AreEqual(new Guid?[] { host.SecondEpisodeId!.Value }, MarkedUnits(payload));
        Assert.AreEqual(1, (await AllRequestsAsync(host)).Count);
        Assert.AreEqual(grabs, host.Environment.Client.Grabs.Count, "The running download of the first episode is not interrupted or duplicated.");
        Assert.AreEqual(existing.Id, second.RequestId);
        CollectionAssert.AreEqual(MarkedUnits(payload), MarkedUnits(PayloadOf(await host.GetAsync(existing.Id))), "A repeat changes nothing.");
    }

    [TestMethod]
    public async Task AProfileThatMayNotRequestLearnsNothingOfSomeoneElsesRequest()
    {
        await using var host = await MovieHostAsync();
        await host.StartAsync();
        await new MediaCapabilityStore(Directory(host)).SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Browse);

        var result = await Intents(host, "bob", AccountRole.User).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual((PlaybackIntentOutcome.NotAvailable, null, null), (result.Outcome, result.RequestId, result.Acquisition));
    }

    [TestMethod]
    public async Task ARequestWhoseMonitoringWasTurnedOffPromisesNothing()
    {
        await using var host = await SeriesHostAsync();
        var scope = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true) { Monitored = false };
        await host.CreateApprovedAsync(scope);

        var result = await Intents(host).StartAsync(host.Work.Id, host.SecondEpisodeId, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.NotAvailable, result.Outcome);
        Assert.IsEmpty(host.Environment.Client.Grabs);
    }

    [TestMethod]
    public async Task ACompletedSeriesRequestWithoutPlayableMediaIsNotAvailableYet()
    {
        await using var host = await SeriesHostAsync();
        var request = await host.CreateApprovedAsync();
        await host.Requests.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Completed, null, null, null, null, CancellationToken.None);
        var projection = new ConsumerAcquisitionQuery(host.Environment.Db, host.Requests, host.Get<VideoRequestWorkResolver>(), TimeProvider.System);

        var empty = await projection.ProjectAsync(await host.GetAsync(request.Id), null, playbackEnabled: true, CancellationToken.None);
        await host.AttachFileAsync(host.EpisodeId);
        var withMedia = await projection.ProjectAsync(await host.GetAsync(request.Id), null, playbackEnabled: true, CancellationToken.None);

        Assert.AreEqual(ConsumerAcquisitionState.NotAvailable, empty.State, "Nothing is playable and nothing keeps looking.");
        Assert.AreEqual(ConsumerAcquisitionState.ReadyToWatch, withMedia.State);
    }

    [TestMethod]
    public async Task WaitingIsClientSideSoTheSharedRequestAndItsDownloadKeepRunning()
    {
        await using var host = await MovieHostAsync();
        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        // The client simply stops polling and asks for nothing more: nothing on the server changes.
        var request = await host.GetAsync(result.RequestId!.Value);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreNotEqual(OperationStatus.Cancelled, (await host.Operations.GetAsync(request.OperationId!.Value))!.Status);
    }

    // ---- Wanted order and housekeeping --------------------------------------------------------------------------------

    [TestMethod]
    public async Task AWantedPassSearchesTheRequestAProfileIsWaitingForFirst()
    {
        await using var host = await MovieHostAsync("Arrival.2016.1080p.WEB-DL.x264-GROUP");
        await host.AddWorkAsync("Arrival", "329865", 2016);
        await host.Requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", "329865", "Arrival", null, null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await Task.Delay(20);
        var waited = new VideoRequestPayload(host.Work.Id, "Dune", 2021, VideoRequestScope.WholeWork, [], MonitorFuture: false) { PlaybackMarkers = [Marker(null)] };
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", host.TmdbId, "Dune", null, null, waited.Serialize());
        await host.Requests.CreateAsync(draft, "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);

        await host.ProcessAsync(DateTime.UtcNow);

        var grabs = host.Environment.Client.Grabs;
        Assert.AreEqual(2, grabs.Count);
        StringAssert.Contains(grabs[0].NzbName!, "Dune", "The older Arrival request waits: Dune has a profile waiting for it.");
        Assert.AreEqual(1, grabs[0].Priority);
        Assert.IsNull(grabs[1].Priority, "Everything else keeps the client's default priority.");
    }

    [TestMethod]
    public async Task AnImportedPlaybackEpisodeLeavesTheRequestsPriorityList()
    {
        await using var host = await SeriesHostAsync();
        var all = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true) { PlaybackMarkers = [Marker(host.SecondEpisodeId!.Value)] };
        var request = await host.StartAsync(all);
        StringAssert.Contains(host.Environment.Client.Grabs.Single().NzbName!, "S01E02", "The playback episode is searched before episode 1.");

        host.CompleteInSabnzbd(request, "/downloads/tv/Severance.S01E02");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        Assert.IsTrue(await host.HasPlayableAsync(host.SecondEpisodeId));
        var after = PayloadOf(await host.GetAsync(request.Id));
        Assert.IsFalse(HasMarker(after), "Once playable, the episode is no longer waited for.");
    }

    // ---- Admin curation, completion races, limits ---------------------------------------------------------------------

    [TestMethod]
    public async Task AnAdminExcludedEpisodeIsNotAcquiredForAProfile()
    {
        await using var host = await SeriesHostAsync();
        var all = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true) { ExcludedEpisodeIds = [host.EpisodeId!.Value] };
        var request = await host.CreateApprovedAsync(all with { NextSearchUtc = DateTime.UtcNow.AddHours(6), Searches = 1 });

        var result = await Intents(host).StartAsync(host.Work.Id, host.EpisodeId, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.NotAvailable, result.Outcome);
        Assert.IsFalse(HasMarker(PayloadOf(await host.GetAsync(request.Id))), "No marker for an episode the Admin curated out.");
        Assert.IsEmpty(host.Environment.Client.Grabs);
    }

    [TestMethod]
    public async Task AdminSavingTheChecklistOrSwitchingMonitoringResetsWhatProfilesWaitFor()
    {
        await using var host = await SeriesHostAsync();
        var monitoring = host.Get<Jularr.Web.Features.Acquisition.Monitoring.VideoMonitoringService>();
        var marked = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true)
        {
            PlaybackMarkers = [Marker(host.SecondEpisodeId!.Value)],
            NextSearchUtc = DateTime.UtcNow.AddHours(6),
            Searches = 1
        };
        var request = await host.CreateApprovedAsync(marked);

        await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.EpisodeId!.Value, monitored: false, CancellationToken.None);
        var afterUncheck = PayloadOf(await host.GetAsync(request.Id));
        CollectionAssert.AreEqual(new Guid?[] { host.SecondEpisodeId!.Value }, MarkedUnits(afterUncheck), "Unchecking another episode leaves the waiting one alone.");
        await monitoring.SetEpisodeMonitoredAsync(host.Work.Id, host.SecondEpisodeId.Value, monitored: false, CancellationToken.None);
        Assert.IsFalse(HasMarker(PayloadOf(await host.GetAsync(request.Id))), "An episode the Admin unchecked is no longer waited for.");
        var third = await host.Environment.Db.WorkEpisodes.SingleAsync(x => x.WorkId == host.Work.Id && x.EpisodeNumber == 3);
        await Intents(host).StartAsync(host.Work.Id, third.Id, CancellationToken.None);
        await Intents(host).StartAsync(host.Work.Id, host.SecondEpisodeId, CancellationToken.None);
        Assert.IsTrue(HasMarker(PayloadOf(await host.GetAsync(request.Id))));
        await monitoring.SetSeriesAsync(host.Work.Id, "off", CancellationToken.None);
        await monitoring.SetSeriesAsync(host.Work.Id, "all", CancellationToken.None);
        var reopened = (await AllRequestsAsync(host)).Single(x => x.IsOpen);
        Assert.IsFalse(HasMarker(PayloadOf(reopened)), "Monitoring off and on again does not bring a marker back.");
    }

    [TestMethod]
    public async Task AnIntentThatAttachesWhileASearchDecidesCompletionKeepsTheRequestOpen()
    {
        await using var host = await SeriesHostAsync();
        await host.AttachFileAsync(host.EpisodeId);
        var scope = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.Custom, [host.EpisodeId!.Value], MonitorFuture: false);
        var request = await host.CreateApprovedAsync(scope);

        // The search has read "episode 1 is available, nothing else requested" when the intent for episode 2 attaches.
        var inner = host.Services.GetServices<IAcquisitionRequestExecutor>().Single();
        var decorated = new AttachAfterExecutor(inner, async () => await Intents(host).StartAsync(host.Work.Id, host.SecondEpisodeId, CancellationToken.None));
        var store = host.Get<AcquisitionAccessStore>();
        var capabilities = new MediaCapabilityService(new MediaCapabilityStore(Directory(host)));
        var settings = new AcquisitionRequestSettingsStore(Directory(host));
        var account = AcquisitionAccessFixture.Account("owner", AccountRole.Owner);
        var service = new AcquisitionRequestService(store, [decorated], account, capabilities, settings, new RecordingEventPublisher(), NullLogger<AcquisitionRequestService>.Instance);

        await service.ContinueAsync(request.Id, CancellationToken.None);

        var after = await host.GetAsync(request.Id);
        Assert.IsTrue(after.IsOpen, "The stale 'all available' result must not complete the request over the new playback unit.");
        CollectionAssert.AreEqual(new Guid?[] { host.SecondEpisodeId!.Value }, MarkedUnits(PayloadOf(after)));
        Assert.AreEqual(1, PayloadOf(after).ScopeRevision, "Attaching is an edit of the request.");
    }

    [TestMethod]
    public async Task AProfileCanHaveOnlyAFewPlaybackRequestsOpenAtOnce()
    {
        await using var host = await SeriesHostAsync();
        var store = host.Get<AcquisitionAccessStore>();
        for (var index = 0; index < PlaybackIntentService.MaxOutstandingPlaybackMarkers; index++)
        {
            var other = await host.AddWorkAsync($"Other {index}", $"90{index}");
            var marked = new VideoRequestPayload(other.Id, other.CanonicalTitle, 2020, VideoRequestScope.WholeWork, [], MonitorFuture: false) { PlaybackMarkers = [Marker(null)] };
            var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Tv, "tmdb", $"90{index}", other.CanonicalTitle, null, null, marked.Serialize());
            await store.CreateAsync(draft, "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        }

        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.LimitReached, result.Outcome);
        Assert.AreEqual(PlaybackIntentService.MaxOutstandingPlaybackMarkers, (await AllRequestsAsync(host)).Count, "No further request, marker or priority.");
        Assert.IsEmpty(host.Environment.Client.Grabs);
        var other2 = await Intents(host, "bob", AccountRole.User).StartAsync(host.Work.Id, null, CancellationToken.None);
        Assert.AreNotEqual(PlaybackIntentOutcome.LimitReached, other2.Outcome, "The limit is per profile.");
    }

    [TestMethod]
    public async Task AnImportedMovieDoesNotKeepItsPlaybackMarker()
    {
        await using var host = await MovieHostAsync();
        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);
        var request = await host.GetAsync(result.RequestId!.Value);
        Assert.IsTrue(MarkedUnits(PayloadOf(request)).Contains(null));

        host.CompleteInSabnzbd(request, "/downloads/movies/Dune.2021");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        var after = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, after.Status);
        Assert.IsFalse(HasMarker(PayloadOf(after)));
    }

    private sealed class AttachAfterExecutor(IAcquisitionRequestExecutor inner, Func<Task> afterDecision) : IAcquisitionRequestExecutor
    {
        public MediaAcquisitionKind Kind => inner.Kind;

        public async Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteAsync(request, cancellationToken);
            await afterDecision();
            return result;
        }
    }

    private sealed class HookedCapabilityService(IMediaCapabilityService inner, Func<int, Task> afterRead) : IMediaCapabilityService
    {
        private int _reads;

        public Task<MediaCapabilityView> GetViewAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default) => inner.GetViewAsync(user, cancellationToken);

        public async Task<MediaCapability> GetEffectiveCapabilityAsync(ClaimsPrincipal? user, WorkMediaType mediaType, CancellationToken cancellationToken = default)
        {
            var capability = await inner.GetEffectiveCapabilityAsync(user, mediaType, cancellationToken);
            await afterRead(++_reads);
            return capability;
        }

        public Task<IReadOnlyList<WorkMediaType>> GetVisibleMediaTypesAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default) => inner.GetVisibleMediaTypesAsync(user, cancellationToken);

        public Task EnsureCapabilityAsync(ClaimsPrincipal? user, WorkMediaType mediaType, MediaCapability required, CancellationToken cancellationToken = default) =>
            inner.EnsureCapabilityAsync(user, mediaType, required, cancellationToken);
    }

    // ---- Marker ownership, lifetime and back-off ----------------------------------------------------------------------

    private static async Task<AcquisitionRequest> MarkedRequestAsync(VideoAcquisitionTestHost host, string title, string tmdbId, PlaybackMarker[] markers, DateTime? nextSearch = null, int searches = 0)
    {
        var work = await host.AddWorkAsync(title, tmdbId);
        var payload = new VideoRequestPayload(work.Id, title, 2020, VideoRequestScope.WholeWork, [], MonitorFuture: false) { PlaybackMarkers = markers, NextSearchUtc = nextSearch, Searches = searches };
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Tv, "tmdb", tmdbId, title, null, null, payload.Serialize());
        return await host.Requests.CreateAsync(draft, "someone", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
    }

    [TestMethod]
    public async Task TheCapCountsTheProfilesOwnLiveMarkersWhoeverMadeTheRequest()
    {
        await using var host = await SeriesHostAsync();
        await new MediaCapabilityStore(Directory(host)).SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Series, MediaCapability.Instant);
        for (var index = 0; index < PlaybackIntentService.MaxOutstandingPlaybackMarkers; index++)
        {
            await MarkedRequestAsync(host, $"Bob {index}", $"70{index}", [Marker(null, "bob")]);
        }

        var bob = await Intents(host, "bob", AccountRole.User).StartAsync(host.Work.Id, null, CancellationToken.None);
        var owner = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.LimitReached, bob.Outcome, "Bob's waits on other people's requests are Bob's.");
        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, owner.Outcome, "The requests' creator is not charged for them.");
    }

    [TestMethod]
    public async Task ExpiredMarkersAndRequestsInALongBackOffNeverLockAProfileOut()
    {
        await using var host = await SeriesHostAsync();
        var now = DateTime.UtcNow;
        for (var index = 0; index < PlaybackIntentService.MaxOutstandingPlaybackMarkers; index++)
        {
            if (index % 2 == 0)
            {
                await MarkedRequestAsync(host, $"Expired {index}", $"71{index}", [Marker(null, "owner", now - VideoRequestPayload.PlaybackTtl - TimeSpan.FromMinutes(1))]);
            }
            else
            {
                await MarkedRequestAsync(host, $"Backoff {index}", $"71{index}", [Marker(null, "owner", now)], nextSearch: now.AddHours(6), searches: 3);
            }
        }

        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, result.Outcome, "Neither an expired wait nor a request that finds no release holds a slot.");
    }

    [TestMethod]
    public void ALapsedMarkerNeitherPrioritizesNorCoversItsEpisode()
    {
        var created = DateTime.UtcNow.AddDays(-1);
        var episode = Guid.NewGuid();
        var payload = new VideoRequestPayload(Guid.NewGuid(), "T", 2020, VideoRequestScope.Custom, [Guid.NewGuid()], MonitorFuture: false);
        var live = payload with { PlaybackMarkers = [Marker(episode, at: DateTime.UtcNow)] };
        var lapsed = payload with { PlaybackMarkers = [Marker(episode, at: DateTime.UtcNow - VideoRequestPayload.PlaybackTtl - TimeSpan.FromSeconds(1))] };

        Assert.IsTrue(new VideoRequestSelection(live, created).Includes(episode, null, null));
        Assert.IsTrue(new VideoRequestSelection(live, created).IsPlaybackEpisode(episode));
        Assert.IsFalse(new VideoRequestSelection(lapsed, created).Includes(episode, null, null), "A stale intent does not stay in the scope.");
        Assert.IsFalse(new VideoRequestSelection(lapsed, created).IsPlaybackEpisode(episode));
        Assert.IsFalse(lapsed.HasPlaybackIntent(DateTime.UtcNow), "Wanted no longer searches the request first.");
    }

    [TestMethod]
    public async Task RepeatedAttachesResetTheBackOffAndSearchInlineOnlyOncePerInterval()
    {
        await using var host = await SeriesHostAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var scope = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.Custom, [host.SecondEpisodeId!.Value], MonitorFuture: false)
        {
            NextSearchUtc = DateTime.UtcNow.AddHours(6),
            Searches = 4
        };
        var request = await host.CreateApprovedAsync(scope);
        var third = await host.Environment.Db.WorkEpisodes.SingleAsync(x => x.WorkId == host.Work.Id && x.EpisodeNumber == 3);
        var intents = Intents(host, clock: clock);

        await intents.StartAsync(host.Work.Id, host.EpisodeId, CancellationToken.None);
        var afterFirst = PayloadOf(await host.GetAsync(request.Id));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "The first intent of the interval searches inline.");
        Assert.IsNotNull(afterFirst.PlaybackResetUtc);

        // A second newly covered unit a minute later adds its marker and its revision but neither resets the back-off nor searches inline.
        clock.Advance(TimeSpan.FromMinutes(1));
        var grabsBefore = host.Environment.Client.Grabs.Count;
        var second = await intents.StartAsync(host.Work.Id, third.Id, CancellationToken.None);
        var afterSecond = PayloadOf(await host.GetAsync(request.Id));

        Assert.AreEqual(afterFirst.PlaybackResetUtc, afterSecond.PlaybackResetUtc, "The reset time only moves when a reset happens.");
        Assert.AreEqual(2, afterSecond.PlaybackMarkers!.Length);
        Assert.AreEqual(afterFirst.ScopeRevision + 1, afterSecond.ScopeRevision);
        Assert.AreEqual(grabsBefore, host.Environment.Client.Grabs.Count, "No inline search inside the interval.");
        Assert.AreEqual(PlaybackIntentOutcome.Acquiring, second.Outcome);
    }

    [TestMethod]
    public async Task AFullMarkerListRefusesInsteadOfEvictingSomeoneElsesWait()
    {
        await using var host = await SeriesHostAsync();
        var markers = Enumerable.Range(0, VideoRequestPayload.MaxPlaybackMarkers).Select(_ => Marker(Guid.NewGuid(), "bob")).ToArray();
        var scope = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true) { PlaybackMarkers = markers };
        var request = await host.CreateApprovedAsync(scope);

        var result = await Intents(host).StartAsync(host.Work.Id, host.EpisodeId, CancellationToken.None);

        Assert.AreEqual(PlaybackIntentOutcome.LimitReached, result.Outcome);
        Assert.AreEqual(markers.Length, PayloadOf(await host.GetAsync(request.Id)).PlaybackMarkers!.Length, "Nobody's marker was dropped.");
    }

    [TestMethod]
    public async Task ReApprovingAFailedRequestStartsWithoutTheOldPlaybackMarkers()
    {
        await using var host = await MovieHostAsync();
        var marked = new VideoRequestPayload(host.Work.Id, "Dune", 2021, VideoRequestScope.WholeWork, [], MonitorFuture: false) { PlaybackMarkers = [Marker(null)] };
        var request = await host.CreateApprovedAsync(marked);
        await host.Requests.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "gave up", null, null, null, CancellationToken.None);
        var capabilities = new MediaCapabilityService(new MediaCapabilityStore(Directory(host)));
        var settings = new AcquisitionRequestSettingsStore(Directory(host));
        var account = AcquisitionAccessFixture.Account("owner", AccountRole.Owner);
        var service = new AcquisitionRequestService(host.Get<AcquisitionAccessStore>(), [], account, capabilities, settings, new RecordingEventPublisher(), NullLogger<AcquisitionRequestService>.Instance);

        await service.ApproveAsync(request.Id, CancellationToken.None);

        Assert.IsFalse(HasMarker(PayloadOf(await host.GetAsync(request.Id))));
    }
}
