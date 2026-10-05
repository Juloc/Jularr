using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Progress;
using Jularr.Web.Ui;

namespace Jularr.Tests;

/// <summary>The pure rules of docs/mockups/instant-play: the next required episode and the primary-action matrix with its capability chain.</summary>
[TestClass]
public sealed class InstantPlayResolverTests
{
    private static readonly DateTime Base = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid WorkId = Guid.NewGuid();

    private static readonly InstantPlayPolicy Everything = new(true, true, true, CanRequest: true, AutoApproves: true, AcquisitionReady: true);
    private static readonly InstantPlayPolicy ApprovalNeeded = Everything with { AutoApproves = false };
    private static readonly InstantPlayPolicy ManagerOnly = Everything with { PlaybackEnabled = false };

    private static SeriesUnit Unit(int season, int number, bool local = true, bool released = true) => new(Guid.NewGuid(), season, number, local, released);

    private static EpisodeProgressState Watched(SeriesUnit unit, int minutes) => new(unit.Id, 0, true, Base.AddMinutes(minutes));

    private static EpisodeProgressState Partial(SeriesUnit unit, int minutes, long positionMs = 600_000) => new(unit.Id, positionMs, false, Base.AddMinutes(minutes));

    private static Dictionary<Guid, EpisodeProgressState> History(params EpisodeProgressState[] rows) => rows.ToDictionary(x => x.EpisodeId);

    private static SeriesPlaybackFacts Series(IReadOnlyList<SeriesUnit> units, Dictionary<Guid, EpisodeProgressState>? progress = null, OpenRequestFacts? open = null, bool identity = true) =>
        new(WorkId, identity, open, units, progress ?? []);

    private static MoviePlaybackFacts Movie(bool local, MediaProgressSnapshot? progress = null, OpenRequestFacts? open = null, bool identity = true) => new(WorkId, identity, open, local, progress);

    private static MediaProgressSnapshot MovieProgress(long positionMs, bool completed) => new(WorkId, null, positionMs, 6_000_000, completed, Base);

    private static OpenRequestFacts Open(AcquisitionRequestStatus status, params Guid[] covers) => new(status, CoversWork: true, covers.ToHashSet());

    private static OpenRequestFacts OpenSeries(AcquisitionRequestStatus status, params Guid[] covers) => new(status, CoversWork: false, covers.ToHashSet());

    private static OpenRequestFacts Pending() => Open(AcquisitionRequestStatus.Pending);

    private static OpenRequestFacts PendingSeries(params Guid[] covers) => OpenSeries(AcquisitionRequestStatus.Pending, covers);

    private static void AssertAction(PrimaryAction action, PrimaryActionKind kind, PrimaryActionReason reason, Guid? episodeId = null, string? message = null)
    {
        Assert.AreEqual((kind, reason, episodeId), (action.Kind, action.Reason, action.WorkEpisodeId), message);
    }

    // ---- The next required episode (SPEC section 4) -------------------------------------------------------------------

    [TestMethod]
    public void NoHistoryStartsAtTheFirstReleasedEpisode()
    {
        var units = new[] { Unit(1, 2), Unit(1, 1, local: false), Unit(0, 1), Unit(1, 3) };

        var next = NextRequiredEpisode.Resolve(units, History())!;

        Assert.AreEqual((units[1].Id, MediaBannerProgressState.NotStarted), (next.Unit.Id, next.State), "The first episode is required even when it is not local; specials are not guessed.");
        Assert.IsNull(NextRequiredEpisode.Resolve([], History()));
        Assert.IsNull(NextRequiredEpisode.Resolve([Unit(1, 1, local: false, released: false)], History()), "Nothing has aired yet.");
    }

    [TestMethod]
    public void ACompletedPrefixLeadsToTheNextEpisodeAfterIt()
    {
        var units = Enumerable.Range(1, 6).Select(number => Unit(1, number, local: number <= 4)).ToArray();
        var progress = History(units.Take(4).Select((unit, index) => Watched(unit, index)).ToArray());

        var next = NextRequiredEpisode.Resolve(units, progress)!;

        Assert.AreEqual((units[4].Id, MediaBannerProgressState.InProgress), (next.Unit.Id, next.State), "Completed through episode 4 needs episode 5, which is not local.");
        Assert.IsFalse(next.Unit.HasMedia);
    }

    [TestMethod]
    public void AResumableEpisodeIsContinuedInsteadOfAcquiringAnother()
    {
        var units = Enumerable.Range(1, 3).Select(number => Unit(1, number, local: number <= 2)).ToArray();
        var progress = History(Watched(units[0], 0), Partial(units[1], 5));

        var next = NextRequiredEpisode.Resolve(units, progress)!;

        Assert.AreEqual((units[1].Id, MediaBannerProgressState.InProgress), (next.Unit.Id, next.State));
    }

    [TestMethod]
    public void AGapInTheLocalFilesIsTheNextRequiredEpisodeAndLaterLocalEpisodesAreNotOffered()
    {
        var units = new[] { Unit(1, 1), Unit(1, 2, local: false), Unit(1, 3) };

        var next = NextRequiredEpisode.Resolve(units, History(Watched(units[0], 0)))!;

        Assert.AreEqual(units[1].Id, next.Unit.Id);
    }

    [TestMethod]
    public void WatchedEpisodesAndUnreleasedOnesAreSkippedAndAFinishedSeriesRewatchesTheFirstLocalEpisode()
    {
        var units = Enumerable.Range(1, 4).Select(number => Unit(1, number, released: number < 4)).Append(Unit(0, 1)).ToArray();
        var progress = History(Watched(units[0], 0), Watched(units[1], 2), Watched(units[2], -5));

        Assert.IsNull(NextRequiredEpisode.Resolve([.. units.Select(x => x with { HasMedia = false })], progress), "Nothing local to watch again.");
        var finished = NextRequiredEpisode.Resolve(units, progress)!;

        Assert.AreEqual((units[0].Id, MediaBannerProgressState.Completed), (finished.Unit.Id, finished.State), "Episode 4 has not aired; specials never block completion.");
    }

    [TestMethod]
    public void ATitleWithOnlySpecialsOffersItsSpecials()
    {
        var specials = new[] { Unit(0, 2), Unit(0, 1) };

        Assert.AreEqual(specials[1].Id, NextRequiredEpisode.Resolve(specials, History())!.Unit.Id);
    }

    // ---- Local media: Play / Continue / Start watching ----------------------------------------------------------------

    [TestMethod]
    public void ALocalMoviePlaysContinuesOrPlaysAgainFromItsCanonicalProgress()
    {
        AssertAction(PrimaryActionResolver.Resolve(Movie(true), Everything), PrimaryActionKind.Play, PrimaryActionReason.LocalMedia);
        AssertAction(PrimaryActionResolver.Resolve(Movie(true, MovieProgress(5_000, false)), Everything), PrimaryActionKind.Play, PrimaryActionReason.LocalMedia, message: "A few seconds is not worth resuming.");
        AssertAction(PrimaryActionResolver.Resolve(Movie(true, MovieProgress(900_000, false)), Everything), PrimaryActionKind.Continue, PrimaryActionReason.ResumePoint);

        var finished = PrimaryActionResolver.Resolve(Movie(true, MovieProgress(0, true)), Everything);
        Assert.AreEqual((PrimaryActionKind.Play, true, true), (finished.Kind, finished.IsRewatch, finished.TargetIsLocal));
        Assert.IsTrue(PrimaryActionResolver.Resolve(Movie(true), ApprovalNeeded).TargetIsLocal);
    }

    [TestMethod]
    public void ALocalSeriesStartsContinuesOrPlaysAgainThroughTheNextRequiredEpisode()
    {
        var units = new[] { Unit(1, 1), Unit(1, 2) };

        var start = PrimaryActionResolver.Resolve(Series(units), Everything);
        AssertAction(start, PrimaryActionKind.StartWatching, PrimaryActionReason.LocalMedia, units[0].Id);
        Assert.IsTrue(start.TargetIsLocal, "Start watching on a local episode opens the player.");

        AssertAction(PrimaryActionResolver.Resolve(Series(units, History(Watched(units[0], 0))), Everything), PrimaryActionKind.Continue, PrimaryActionReason.ResumePoint, units[1].Id);
        AssertAction(PrimaryActionResolver.Resolve(Series(units, History(Partial(units[0], 0))), Everything), PrimaryActionKind.Continue, PrimaryActionReason.ResumePoint, units[0].Id);

        var finished = PrimaryActionResolver.Resolve(Series(units, History(Watched(units[0], 0), Watched(units[1], 1))), Everything);
        Assert.AreEqual((PrimaryActionKind.Play, units[0].Id, true), (finished.Kind, finished.WorkEpisodeId, finished.IsRewatch));
    }

    [TestMethod]
    public void AnExplicitEpisodeIsTheTargetWhateverTheProgressSays()
    {
        var units = new[] { Unit(1, 1), Unit(1, 2), Unit(1, 3, local: false) };
        var progress = History(Watched(units[0], 0), Partial(units[1], 1));

        AssertAction(PrimaryActionResolver.Resolve(Series(units, progress), Everything, units[0].Id), PrimaryActionKind.Play, PrimaryActionReason.LocalMedia, units[0].Id);
        Assert.IsTrue(PrimaryActionResolver.Resolve(Series(units, progress), Everything, units[0].Id).IsRewatch);
        AssertAction(PrimaryActionResolver.Resolve(Series(units, progress), Everything, units[1].Id), PrimaryActionKind.Continue, PrimaryActionReason.ResumePoint, units[1].Id);
        AssertAction(PrimaryActionResolver.Resolve(Series(units, progress), Everything, units[2].Id), PrimaryActionKind.WatchNow, PrimaryActionReason.InstantAcquisition, units[2].Id);
        AssertAction(PrimaryActionResolver.Resolve(Series(units, progress), Everything, Guid.NewGuid()), PrimaryActionKind.None, PrimaryActionReason.UnknownTarget);
    }

    // ---- Missing media: the capability chain --------------------------------------------------------------------------

    [TestMethod]
    public void MissingMediaOnAPlayingInstanceStartsWatchingForASeriesAndWatchesNowForAMovie()
    {
        var units = new[] { Unit(1, 1, local: false), Unit(1, 2, local: false) };

        var series = PrimaryActionResolver.Resolve(Series(units), Everything);
        AssertAction(series, PrimaryActionKind.StartWatching, PrimaryActionReason.InstantAcquisition, units[0].Id);
        Assert.IsFalse(series.TargetIsLocal);
        AssertAction(PrimaryActionResolver.Resolve(Movie(false), Everything), PrimaryActionKind.WatchNow, PrimaryActionReason.InstantAcquisition);
    }

    [TestMethod]
    public void WhenApprovalIsRequiredOrAcquisitionIsNotReadyTheActionIsRequest()
    {
        var units = new[] { Unit(1, 1, local: false) };

        AssertAction(PrimaryActionResolver.Resolve(Series(units), ApprovalNeeded), PrimaryActionKind.Request, PrimaryActionReason.ApprovalRequired, units[0].Id);
        AssertAction(PrimaryActionResolver.Resolve(Movie(false), ApprovalNeeded), PrimaryActionKind.Request, PrimaryActionReason.ApprovalRequired);
        AssertAction(PrimaryActionResolver.Resolve(Movie(false), Everything with { AcquisitionReady = false }), PrimaryActionKind.Request, PrimaryActionReason.AcquisitionNotReady);
    }

    [TestMethod]
    public void ManagerOnlyNeverPlaysAndRequestsMissingMedia()
    {
        var units = new[] { Unit(1, 1), Unit(1, 2, local: false) };

        AssertAction(PrimaryActionResolver.Resolve(Movie(false), ManagerOnly), PrimaryActionKind.Request, PrimaryActionReason.PlaybackDisabled);
        AssertAction(PrimaryActionResolver.Resolve(Series(units, History(Watched(units[0], 0))), ManagerOnly), PrimaryActionKind.Request, PrimaryActionReason.PlaybackDisabled, units[1].Id);

        var available = PrimaryActionResolver.Resolve(Movie(true, MovieProgress(900_000, false)), ManagerOnly);
        AssertAction(available, PrimaryActionKind.Available, PrimaryActionReason.PlaybackDisabled);
        AssertAction(PrimaryActionResolver.Resolve(Series(units), ManagerOnly), PrimaryActionKind.Available, PrimaryActionReason.PlaybackDisabled, units[0].Id);

        foreach (var facts in new PlaybackFacts[] { Movie(true), Movie(false), Series(units), Series([Unit(1, 1, local: false)]), Movie(false, open: Open(AcquisitionRequestStatus.Searching)) })
        {
            var kind = PrimaryActionResolver.Resolve(facts, ManagerOnly).Kind;
            CollectionAssert.DoesNotContain(new[] { PrimaryActionKind.Play, PrimaryActionKind.Continue, PrimaryActionKind.StartWatching, PrimaryActionKind.WatchNow }, kind);
        }
    }

    [TestMethod]
    public void WithoutPermissionOrModulesThereIsNoAcquisitionAction()
    {
        AssertAction(PrimaryActionResolver.Resolve(Movie(false), Everything with { CanRequest = false, AutoApproves = false }), PrimaryActionKind.None, PrimaryActionReason.NotPermitted);
        AssertAction(PrimaryActionResolver.Resolve(Movie(false), Everything with { AcquisitionEnabled = false }), PrimaryActionKind.None, PrimaryActionReason.AcquisitionDisabled);
        AssertAction(PrimaryActionResolver.Resolve(Movie(false), Everything with { MediaTypeEnabled = false }), PrimaryActionKind.None, PrimaryActionReason.MediaTypeDisabled);
        AssertAction(PrimaryActionResolver.Resolve(Movie(false, identity: false), Everything), PrimaryActionKind.None, PrimaryActionReason.NoProviderIdentity);
        Assert.AreEqual(PrimaryActionKind.Play, PrimaryActionResolver.Resolve(Movie(true), Everything with { AcquisitionEnabled = false, CanRequest = false }).Kind, "Local media plays whatever acquisition allows.");
    }

    [TestMethod]
    public void AnEpisodeThatHasNotAiredCannotBeAcquiredButARequestStillCanMonitorIt()
    {
        var units = new[] { Unit(1, 1), Unit(1, 2, local: false, released: false) };
        var progress = History(Watched(units[0], 0));

        AssertAction(PrimaryActionResolver.Resolve(Series(units, progress), Everything, units[1].Id), PrimaryActionKind.Request, PrimaryActionReason.NotReleased, units[1].Id);
        var caughtUp = PrimaryActionResolver.Resolve(Series(units, progress), Everything);
        Assert.AreEqual((PrimaryActionKind.Play, true), (caughtUp.Kind, caughtUp.IsRewatch), "Everything aired is watched: the first local episode is offered again.");

        var none = PrimaryActionResolver.Resolve(Series([Unit(1, 1, local: false, released: false)]), Everything);
        AssertAction(none, PrimaryActionKind.Request, PrimaryActionReason.NothingToWatch);
    }

    // ---- An equivalent request is active ------------------------------------------------------------------------------

    [TestMethod]
    public void AnActiveRequestThatCoversTheTargetShowsItsStateInsteadOfAcquiringAgain()
    {
        var units = new[] { Unit(1, 1, local: false) };

        AssertAction(PrimaryActionResolver.Resolve(Movie(false, open: Open(AcquisitionRequestStatus.Downloading)), Everything), PrimaryActionKind.ShowRequestState, PrimaryActionReason.RequestActive);
        AssertAction(PrimaryActionResolver.Resolve(Series(units, open: OpenSeries(AcquisitionRequestStatus.Searching, units[0].Id)), Everything), PrimaryActionKind.ShowRequestState, PrimaryActionReason.RequestActive, units[0].Id);
        AssertAction(PrimaryActionResolver.Resolve(Movie(false, open: Pending()), Everything), PrimaryActionKind.ShowRequestState, PrimaryActionReason.AwaitingApproval, message: "Approval is never bypassed.");
        AssertAction(PrimaryActionResolver.Resolve(Movie(false, open: Open(AcquisitionRequestStatus.Searching)), ManagerOnly), PrimaryActionKind.ShowRequestState, PrimaryActionReason.RequestActive);
        Assert.AreEqual(PrimaryActionKind.Play, PrimaryActionResolver.Resolve(Movie(true, open: Open(AcquisitionRequestStatus.Downloading)), Everything).Kind, "Playable content wins over an open request.");
    }

    [TestMethod]
    public void AnApprovedRequestThatDoesNotCoverTheTargetMayTakeItAsAPlaybackIntentButAPendingOneNever()
    {
        var units = new[] { Unit(1, 1, local: false), Unit(1, 2, local: false) };

        var approved = Series(units, open: OpenSeries(AcquisitionRequestStatus.Approved, units[1].Id));
        AssertAction(PrimaryActionResolver.Resolve(approved, Everything), PrimaryActionKind.StartWatching, PrimaryActionReason.InstantAcquisition, units[0].Id);
        AssertAction(PrimaryActionResolver.Resolve(approved, ApprovalNeeded), PrimaryActionKind.ShowRequestState, PrimaryActionReason.RequestActive, units[0].Id);

        var pending = Series(units, open: PendingSeries(units[1].Id));
        AssertAction(PrimaryActionResolver.Resolve(pending, Everything), PrimaryActionKind.ShowRequestState, PrimaryActionReason.AwaitingApproval, units[0].Id);
    }
}
