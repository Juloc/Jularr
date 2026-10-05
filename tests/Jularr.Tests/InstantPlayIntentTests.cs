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
    private static PlaybackIntentService Intents(VideoAcquisitionTestHost host, string profile = "owner", AccountRole role = AccountRole.Owner, Func<int, Task>? afterCapabilityRead = null, bool playback = true)
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
        return new PlaybackIntentService(facts, policies, requests, store, new ConsumerAcquisitionQuery(db, host.Get<VideoRequestWorkResolver>(), TimeProvider.System), account);
    }

    private static async Task<IReadOnlyList<AcquisitionRequest>> AllRequestsAsync(VideoAcquisitionTestHost host) =>
        await host.Requests.ListAllAsync(100, CancellationToken.None);

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
        var other = await host.AddWorkAsync("Arrival", "329865");

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
        Assert.AreEqual((VideoRequestScope.WholeWork, false, true), (payload.Scope, payload.MonitorFuture, payload.PlaybackWork));
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
        CollectionAssert.AreEqual(new[] { host.EpisodeId.Value }, payload.PlaybackEpisodeIds);
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
        CollectionAssert.AreEqual(new[] { host.EpisodeId!.Value }, payload.PlaybackEpisodeIds);
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
    public async Task StopWaitingHasNoServerCounterpartAndSharedAcquisitionKeepsRunning()
    {
        await using var host = await MovieHostAsync();
        var result = await Intents(host).StartAsync(host.Work.Id, null, CancellationToken.None);

        var methods = typeof(PlaybackIntentService).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(x => x.Name).ToArray();
        CollectionAssert.AreEqual(new[] { nameof(PlaybackIntentService.StartAsync) }, methods, "The only server action is starting an intent; waiting is a client-side intent.");

        // The client simply stops polling: the request and its download are untouched.
        var request = await host.GetAsync(result.RequestId!.Value);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreNotEqual(OperationStatus.Cancelled, (await host.Operations.GetAsync(request.OperationId!.Value))!.Status);
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
        CollectionAssert.AreEqual(new[] { host.SecondEpisodeId!.Value }, payload.PlaybackEpisodeIds);
        Assert.AreEqual(1, (await AllRequestsAsync(host)).Count);
        Assert.AreEqual(grabs, host.Environment.Client.Grabs.Count, "The running download of the first episode is not interrupted or duplicated.");
        Assert.AreEqual(existing.Id, second.RequestId);
        CollectionAssert.AreEqual(payload.PlaybackEpisodeIds, PayloadOf(await host.GetAsync(existing.Id)).PlaybackEpisodeIds, "A repeat changes nothing.");
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
        var projection = new ConsumerAcquisitionQuery(host.Environment.Db, host.Get<VideoRequestWorkResolver>(), TimeProvider.System);

        var empty = await projection.ProjectAsync(await host.GetAsync(request.Id), null, playbackEnabled: true, CancellationToken.None);
        await host.AttachFileAsync(host.EpisodeId);
        var withMedia = await projection.ProjectAsync(await host.GetAsync(request.Id), null, playbackEnabled: true, CancellationToken.None);

        Assert.AreEqual(ConsumerAcquisitionState.NotAvailableYet, empty.State);
        Assert.AreEqual(ConsumerAcquisitionState.ReadyToWatch, withMedia.State);
    }

    // ---- Wanted order and housekeeping --------------------------------------------------------------------------------

    [TestMethod]
    public async Task AWantedPassSearchesTheRequestAProfileIsWaitingForFirst()
    {
        await using var host = await MovieHostAsync("Arrival.2016.1080p.WEB-DL.x264-GROUP");
        await host.AddWorkAsync("Arrival", "329865");
        await host.Requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", "329865", "Arrival", null, null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await Task.Delay(20);
        var waited = new VideoRequestPayload(host.Work.Id, "Dune", 2021, VideoRequestScope.WholeWork, [], MonitorFuture: false) { PlaybackWork = true };
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
        var all = new VideoRequestPayload(host.Work.Id, host.Work.CanonicalTitle, host.Work.Year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true) { PlaybackEpisodeIds = [host.SecondEpisodeId!.Value] };
        var request = await host.StartAsync(all);
        StringAssert.Contains(host.Environment.Client.Grabs.Single().NzbName!, "S01E02", "The playback episode is searched before episode 1.");

        host.CompleteInSabnzbd(request, "/downloads/tv/Severance.S01E02");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        Assert.IsTrue(await host.HasPlayableAsync(host.SecondEpisodeId));
        var after = PayloadOf(await host.GetAsync(request.Id));
        Assert.IsFalse(after.HasPlaybackIntent, "Once playable, the episode is no longer waited for.");
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
}
