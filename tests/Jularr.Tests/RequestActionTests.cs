using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

/// <summary>
/// Cancel, Edit and Retry of a request (docs/mockups/request-status-details, sections 9 to 11): who may do them, that each is decided by one
/// compare-and-set on the stored status, and that a request somebody else decided first is reported, never overwritten.
/// </summary>
[TestClass]
public sealed class RequestActionTests
{
    [TestMethod]
    public async Task CancellingARequestEndsItAsCancelledAndRepeatingItChangesNothing()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);
        var request = await alice.SubmitAsync(BookDraft("dune"), CancellationToken.None);

        Assert.AreEqual(RequestCancelOutcome.Cancelled, await alice.CancelAsync(request.Id, CancellationToken.None));
        var cancelled = (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!;
        Assert.IsTrue(cancelled.IsCancelled);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, cancelled.Status);
        Assert.IsNull(cancelled.DecidedByProfileId, "Cancelling is not a decision of an approver.");

        Assert.AreEqual(RequestCancelOutcome.AlreadyCancelled, await alice.CancelAsync(request.Id, CancellationToken.None));
        Assert.AreEqual(cancelled.UpdatedAt, (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.UpdatedAt, "A repeated cancel writes nothing.");
    }

    [TestMethod]
    public async Task ACancelThatArrivesAfterTheApprovalLeavesTheApprovedRequestAlone()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var alice = fixture.Service("alice", AccountRole.User, executor);
        var owner = fixture.Service("owner", AccountRole.Owner, executor);
        var request = await alice.SubmitAsync(BookDraft("dune"), CancellationToken.None);

        await owner.ApproveAsync(request.Id, CancellationToken.None);

        Assert.AreEqual(RequestCancelOutcome.NoLongerPending, await alice.CancelAsync(request.Id, CancellationToken.None));
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Status, "The acquisition the approval started keeps running.");
    }

    [TestMethod]
    public async Task OnlyTheRequesterOrAMediaManagerMayCancelARequest()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);
        var bob = fixture.Service("bob", AccountRole.User);
        var owner = fixture.Service("owner", AccountRole.Owner);
        var request = await alice.SubmitAsync(BookDraft("dune"), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => bob.CancelAsync(request.Id, CancellationToken.None));
        Assert.AreEqual(AcquisitionRequestStatus.Pending, (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Status);

        Assert.AreEqual(RequestCancelOutcome.Cancelled, await owner.CancelAsync(request.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task AnApprovalOrRejectionThatArrivesAfterTheCancelLeavesTheRequestCancelled()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var alice = fixture.Service("alice", AccountRole.User, executor);
        var owner = fixture.Service("owner", AccountRole.Owner, executor);
        var request = await alice.SubmitAsync(BookDraft("dune"), CancellationToken.None);
        await alice.CancelAsync(request.Id, CancellationToken.None);

        var approved = await owner.ApproveAsync(request.Id, CancellationToken.None);
        await owner.RejectAsync(request.Id, "Late.", CancellationToken.None);

        Assert.IsTrue(approved.IsCancelled);
        Assert.AreEqual(0, executor.Runs, "Nothing is acquired for a cancelled request.");
        var stored = (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!;
        Assert.IsTrue(stored.IsCancelled);
        Assert.AreEqual(0, fixture.Events.Published.Count(item => item.Category == Jularr.Web.Features.Events.JularrEventCategory.RequestApproved));
    }

    [TestMethod]
    public async Task AManagerWhoCancelsForSomebodyElseIsRecordedAndARejectNoteCannotImitateACancel()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);
        var owner = fixture.Service("owner", AccountRole.Owner);
        var cancelled = await alice.SubmitAsync(BookDraft("dune"), CancellationToken.None);
        var rejected = await alice.SubmitAsync(BookDraft("emma"), CancellationToken.None);

        await owner.CancelAsync(cancelled.Id, CancellationToken.None);
        await owner.RejectAsync(rejected.Id, AcquisitionRequest.CancelledMessage, CancellationToken.None);

        Assert.AreEqual("owner", (await fixture.Store.GetAsync(cancelled.Id, CancellationToken.None))!.DecidedByProfileId);
        var stored = (await fixture.Store.GetAsync(rejected.Id, CancellationToken.None))!;
        Assert.IsFalse(stored.IsCancelled, "A rejection is never shown to the requester as their own cancel.");
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, stored.Status);
    }

    [TestMethod]
    public async Task ARejectedRequestIsNotACancelledOne()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);
        var owner = fixture.Service("owner", AccountRole.Owner);
        var request = await alice.SubmitAsync(BookDraft("dune"), CancellationToken.None);

        await owner.RejectAsync(request.Id, "Not now.", CancellationToken.None);

        Assert.IsFalse((await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.IsCancelled);
        Assert.AreEqual(RequestCancelOutcome.NoLongerPending, await alice.CancelAsync(request.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task RetryRunsAFailedRequestAgainOnceAndNeverFromAnotherProfile()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var failing = new RecordingExecutor(MediaAcquisitionKind.Book, fail: true);
        var owner = fixture.Service("owner", AccountRole.Owner, failing);
        var failed = await owner.SubmitAsync(BookDraft("dune"), CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, failed.Status);

        var working = new RecordingExecutor(MediaAcquisitionKind.Book);
        var retrying = fixture.Service("owner", AccountRole.Owner, working);
        var stranger = fixture.Service("bob", AccountRole.User, working);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => stranger.RetryAsync(failed.Id, CancellationToken.None));
        Assert.AreEqual(0, working.Runs);

        Assert.AreEqual(RequestRetryOutcome.Retried, await retrying.RetryAsync(failed.Id, CancellationToken.None));
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await fixture.Store.GetAsync(failed.Id, CancellationToken.None))!.Status);
        Assert.AreEqual(1, working.Runs);

        Assert.AreEqual(RequestRetryOutcome.AlreadyRetried, await retrying.RetryAsync(failed.Id, CancellationToken.None), "A second retry does not start a second run and is not an error.");
        Assert.AreEqual(1, working.Runs);
    }

    [TestMethod]
    public async Task RetryDoesNotReopenATitleThatHasAnotherOpenRequest()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var owner = fixture.Service("owner", AccountRole.Owner, new RecordingExecutor(MediaAcquisitionKind.Book, fail: true));
        var failed = await owner.SubmitAsync(BookDraft("dune"), CancellationToken.None);
        var newer = await fixture.Service("owner", AccountRole.Owner, new RecordingExecutor(MediaAcquisitionKind.Book)).SubmitAsync(BookDraft("dune"), CancellationToken.None);
        Assert.AreNotEqual(failed.Id, newer.Id);
        var retryExecutor = new RecordingExecutor(MediaAcquisitionKind.Book);

        Assert.AreEqual(RequestRetryOutcome.NotRetryable, await fixture.Service("owner", AccountRole.Owner, retryExecutor).RetryAsync(failed.Id, CancellationToken.None));
        Assert.AreEqual(AcquisitionRequestStatus.Failed, (await fixture.Store.GetAsync(failed.Id, CancellationToken.None))!.Status);
        Assert.AreEqual(0, retryExecutor.Runs);
    }

    [TestMethod]
    public async Task RetryOfARequestThatIsNotFailedIsRefused()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var alice = fixture.Service("alice", AccountRole.User, executor);
        var request = await alice.SubmitAsync(BookDraft("dune"), CancellationToken.None);

        Assert.AreEqual(RequestRetryOutcome.NotRetryable, await alice.RetryAsync(request.Id, CancellationToken.None));
        Assert.AreEqual(0, executor.Runs);
    }

    [TestMethod]
    public async Task EditingASeriesRequestChangesItsScopeAndKeepsEverythingElse()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var (work, season) = await SeriesAsync(fixture);
        var original = new VideoRequestPayload(work.Id, work.CanonicalTitle, 2022, VideoRequestScope.AllCurrentAndFuture, [], true) { ScopeRevision = 3 };
        var request = await fixture.Store.CreateAsync(TvDraft(original), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var scoped = await new VideoRequestScopeResolver(fixture.Db).BuildTvPayloadAsync(work.Id, new VideoRequestScopeChoice(VideoRequestScope.Custom, [season.Id], [], MonitorFuture: false), CancellationToken.None);

        Assert.AreEqual(RequestEditOutcome.Saved, await fixture.Service("alice", AccountRole.User).EditAsync(request.Id, scoped, null, CancellationToken.None));

        var saved = VideoRequestPayload.Parse((await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.PayloadJson)!;
        Assert.AreEqual(VideoRequestScope.Custom, saved.Scope);
        CollectionAssert.AreEqual(new[] { season.Id }, saved.SelectedSeasonIds);
        Assert.IsFalse(saved.MonitorFuture);
        Assert.AreEqual(work.Id, saved.WorkId);
        Assert.AreEqual(4, saved.ScopeRevision, "A search that started before the edit can tell it happened.");
        Assert.AreEqual(AcquisitionRequestStatus.Pending, (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Status);
    }

    [TestMethod]
    public async Task EditingAnAnimeRequestChangesItsLanguagesAndKeepsItsScope()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var options = new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [1, 2], AudioLanguage = "ja", QualityProfileId = "anime-1080p" }.Validate();
        var request = await fixture.Store.CreateAsync(AnimeDraft("154587", options), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);

        var outcome = await fixture.Service("alice", AccountRole.User).EditAsync(request.Id, null, new AcquisitionRequestOptions { AudioLanguage = "de", SubtitleLanguage = "en" }, CancellationToken.None);

        Assert.AreEqual(RequestEditOutcome.Saved, outcome);
        var saved = (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Options;
        Assert.AreEqual("de", saved.AudioLanguage);
        Assert.AreEqual("en", saved.SubtitleLanguage);
        Assert.AreEqual(RequestScope.Seasons, saved.Scope);
        CollectionAssert.AreEqual(new[] { 1, 2 }, saved.Seasons.ToArray());
        Assert.AreEqual("anime-1080p", saved.QualityProfileId);
    }

    [TestMethod]
    public async Task AnEditAfterTheApprovalIsRefusedAndTheApprovedIntentStays()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var options = new AcquisitionRequestOptions { AudioLanguage = "ja" }.Validate();
        var request = await fixture.Store.CreateAsync(AnimeDraft("1", options), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await fixture.Store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Approved, null, null, null, "owner", CancellationToken.None);

        var outcome = await fixture.Service("alice", AccountRole.User).EditAsync(request.Id, null, new AcquisitionRequestOptions { AudioLanguage = "de" }, CancellationToken.None);

        Assert.AreEqual(RequestEditOutcome.NotEditable, outcome);
        Assert.AreEqual("ja", (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Options.AudioLanguage);
    }

    [TestMethod]
    public async Task OnlyRequestsWithSettingsAreEditableAndOnlyByTheirRequester()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var book = await fixture.Store.CreateAsync(BookDraft("dune"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var anime = await fixture.Store.CreateAsync(AnimeDraft("2", null), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var alice = fixture.Service("alice", AccountRole.User);

        Assert.IsFalse(book.CanBeEdited, "A book request has no settings to edit.");
        Assert.AreEqual(RequestEditOutcome.NotEditable, await alice.EditAsync(book.Id, null, new AcquisitionRequestOptions { AudioLanguage = "de" }, CancellationToken.None));
        Assert.AreEqual(RequestEditOutcome.NotEditable, await alice.EditAsync(anime.Id, null, null, CancellationToken.None), "An edit that carries nothing for the request's media type is not an edit.");
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => fixture.Service("bob", AccountRole.User).EditAsync(anime.Id, null, new AcquisitionRequestOptions { AudioLanguage = "de" }, CancellationToken.None));
    }

    [TestMethod]
    public void TheActionsThatAreAllowedFollowTheStatusOfTheRequest()
    {
        AcquisitionRequest Make(AcquisitionRequestStatus status, MediaAcquisitionKind kind = MediaAcquisitionKind.Tv, string? message = null) =>
            new(Guid.NewGuid(), kind, "tmdb", "1", "Title", null, null, null, "alice", status, message, null, null, DateTime.UtcNow, DateTime.UtcNow, null, null);

        var pending = Make(AcquisitionRequestStatus.Pending);
        Assert.IsTrue(pending.CanBeCancelled);
        Assert.IsTrue(pending.CanBeEdited);
        Assert.IsFalse(Make(AcquisitionRequestStatus.Pending, MediaAcquisitionKind.Movie).CanBeEdited);
        Assert.IsFalse(Make(AcquisitionRequestStatus.Approved).CanBeCancelled);
        Assert.IsFalse(Make(AcquisitionRequestStatus.Searching).CanBeEdited);
        Assert.IsTrue(Make(AcquisitionRequestStatus.Failed).CanBeRetried);
        Assert.IsFalse(Make(AcquisitionRequestStatus.Rejected).CanBeRetried);
        Assert.IsTrue(Make(AcquisitionRequestStatus.Rejected, message: AcquisitionRequest.CancelledMessage).IsCancelled);
        Assert.IsFalse(Make(AcquisitionRequestStatus.Rejected, message: "Not suitable.").IsCancelled);
    }

    private static async Task<(Work Work, WorkSeason Season)> SeriesAsync(AcquisitionAccessFixture fixture)
    {
        var work = await new LibraryCanonicalSeed(fixture.Db).AddWorkAsync(WorkMediaType.Series, "The Long Winter", 2022);
        var season = new WorkSeason { WorkId = work.Id, SeasonNumber = 1 };
        fixture.Db.Add(season);
        await fixture.Db.SaveChangesAsync();
        return (work, season);
    }

    private static AcquisitionRequestDraft BookDraft(string id) => new(MediaAcquisitionKind.Book, "test", id, id.ToUpperInvariant(), "Author", null);

    private static AcquisitionRequestDraft AnimeDraft(string id, AcquisitionRequestOptions? options) => new(MediaAcquisitionKind.Anime, "anilist", id, $"Title {id}", null, null, options?.ToPayloadJson());

    private static AcquisitionRequestDraft TvDraft(VideoRequestPayload payload) => new(MediaAcquisitionKind.Tv, "tmdb", "9503", payload.Title, null, null, payload.Serialize());
}
