using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using DetailsModel = Jularr.Web.Pages.Requests.DetailsModel;

namespace Jularr.Tests;

/// <summary>
/// The handlers behind the request status surface: each is scoped to the signed-in profile's own request, answers with the panel as the
/// request is now, and leaves the decision to <see cref="AcquisitionRequestService"/>.
/// </summary>
[TestClass]
public sealed class RequestDetailsHandlerTests
{
    [TestMethod]
    public async Task CancelAnswersWithThePanelOfTheCancelledRequestAndRedirectsWithoutScript()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var request = await fixture.Store.CreateAsync(Book("dune"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var page = Page(fixture, "alice");

        var panel = (PartialViewResult)await page.OnPostCancelAsync(request.Id, panel: true, CancellationToken.None);

        Assert.AreEqual("_RequestStatusPanel", panel.ViewName);
        Assert.IsTrue(page.Panel.View.Request.IsCancelled);
        Assert.IsNull(page.Panel.Notice);

        var again = await fixture.Store.CreateAsync(Book("again"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var redirect = (RedirectResult)await page.OnPostCancelAsync(again.Id, panel: false, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestService.StatusPath(again.Id), redirect.Url);
    }

    [TestMethod]
    public async Task CancellingARequestThatWasApprovedInTheMeantimeSaysSoAndChangesNothing()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var request = await fixture.Store.CreateAsync(Book("dune"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await fixture.Store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Approved, null, null, null, "owner", CancellationToken.None);
        var page = Page(fixture, "alice");

        await page.OnPostCancelAsync(request.Id, panel: true, CancellationToken.None);

        Assert.AreEqual("requests.detail.error.changed", page.Panel.Notice);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Status);

        var redirect = (RedirectResult)await page.OnPostCancelAsync(request.Id, panel: false, CancellationToken.None);
        StringAssert.EndsWith(redirect.Url, "?notice=changed");
    }

    [TestMethod]
    public async Task NoHandlerTouchesARequestOfAnotherProfile()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var theirs = await fixture.Store.CreateAsync(Anime("1"), "bob", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var failed = await fixture.Store.CreateAsync(Anime("2"), "bob", AcquisitionRequestStatus.Failed, "owner", CancellationToken.None);
        var page = Page(fixture, "alice");

        Assert.IsInstanceOfType<NotFoundResult>(await page.OnGetAsync(theirs.Id, null, CancellationToken.None));
        Assert.IsInstanceOfType<NotFoundResult>(await page.OnGetPanelAsync(theirs.Id, CancellationToken.None));
        Assert.IsInstanceOfType<NotFoundResult>(await page.OnPostCancelAsync(theirs.Id, true, CancellationToken.None));
        Assert.IsInstanceOfType<NotFoundResult>(await page.OnPostRetryAsync(failed.Id, true, CancellationToken.None));
        Assert.IsInstanceOfType<NotFoundResult>(await page.OnGetEditAsync(theirs.Id, CancellationToken.None));
        Assert.IsInstanceOfType<NotFoundResult>(await page.OnPostEditAsync(theirs.Id, new DiscoverRequestForm { Audio = "de" }, CancellationToken.None));
        Assert.AreEqual(AcquisitionRequestStatus.Pending, (await fixture.Store.GetAsync(theirs.Id, CancellationToken.None))!.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, (await fixture.Store.GetAsync(failed.Id, CancellationToken.None))!.Status);
    }

    [TestMethod]
    public async Task RetryRunsTheFailedRequestAgainAndShowsWhereItStandsNow()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var request = await fixture.Store.CreateAsync(Book("dune"), "alice", AcquisitionRequestStatus.Failed, "owner", CancellationToken.None);
        var page = Page(fixture, "alice", executor);

        await page.OnPostRetryAsync(request.Id, panel: true, CancellationToken.None);

        Assert.AreEqual(1, executor.Runs);
        Assert.AreEqual(ConsumerAcquisitionState.GettingMedia, page.Panel.View.State.State);
        Assert.IsNull(page.Panel.Notice);

        await page.OnPostRetryAsync(request.Id, panel: true, CancellationToken.None);
        Assert.AreEqual(1, executor.Runs, "A second retry of the same request starts nothing.");
        Assert.IsNull(page.Panel.Notice, "Repeating a retry is not an error.");
    }

    [TestMethod]
    public async Task EditOffersTheSavedSettingsAndSavesWhatTheDialogSends()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var request = await fixture.Store.CreateAsync(Anime("1", new AcquisitionRequestOptions { AudioLanguage = "ja" }.Validate()), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var page = Page(fixture, "alice");

        var settings = (PartialViewResult)await page.OnGetEditAsync(request.Id, CancellationToken.None);
        var view = (DiscoverRequestSettingsView)settings.Model!;
        Assert.AreEqual("ja", view.DefaultAudio);
        Assert.IsTrue(view.OffersLanguage);
        Assert.IsNull(view.Existing, "Editing never offers the duplicate-request state of a new request.");

        var saved = (PartialViewResult)await page.OnPostEditAsync(request.Id, new DiscoverRequestForm { Audio = "de", Subtitles = "en" }, CancellationToken.None);
        var result = (DiscoverRequestResultView)saved.Model!;
        Assert.IsTrue(result.Updated);
        Assert.AreEqual("de", (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.Options.AudioLanguage);
    }

    [TestMethod]
    public async Task EditRefusesWhatTheRequestCannotTakeAndWhatIsNoLongerPending()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var anime = await fixture.Store.CreateAsync(Anime("1"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var book = await fixture.Store.CreateAsync(Book("dune"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var approved = await fixture.Store.CreateAsync(Anime("2"), "alice", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var page = Page(fixture, "alice");

        Assert.AreEqual(StatusCodes.Status409Conflict, ((StatusCodeResult)await page.OnGetEditAsync(book.Id, CancellationToken.None)).StatusCode);
        Assert.AreEqual(StatusCodes.Status409Conflict, ((StatusCodeResult)await page.OnGetEditAsync(approved.Id, CancellationToken.None)).StatusCode);
        Assert.AreEqual(StatusCodes.Status409Conflict, ((StatusCodeResult)await page.OnPostEditAsync(approved.Id, new DiscoverRequestForm { Audio = "de" }, CancellationToken.None)).StatusCode);
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostEditAsync(anime.Id, new DiscoverRequestForm { Audio = "off" }, CancellationToken.None));
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostEditAsync(anime.Id, new DiscoverRequestForm { Scope = "all" }, CancellationToken.None), "An anime request has no series scope.");
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostEditAsync(book.Id, new DiscoverRequestForm { Audio = "de" }, CancellationToken.None));
        Assert.IsNull((await fixture.Store.GetAsync(anime.Id, CancellationToken.None))!.Options.AudioLanguage);
    }

    [TestMethod]
    public async Task EditingASeriesPreselectsTheSavedScopeAndSavesTheNewOne()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = await new LibraryCanonicalSeed(fixture.Db).AddWorkAsync(WorkMediaType.Series, "The Long Winter", 2022);
        var first = new WorkSeason { WorkId = work.Id, SeasonNumber = 1 };
        var second = new WorkSeason { WorkId = work.Id, SeasonNumber = 2 };
        fixture.Db.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();
        var saved = new VideoRequestPayload(work.Id, work.CanonicalTitle, 2022, VideoRequestScope.Custom, [], true, SelectedSeasonIds: [first.Id]);
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Tv, "tmdb", "9503", work.CanonicalTitle, null, null, saved.Serialize());
        var request = await fixture.Store.CreateAsync(draft, "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var page = Page(fixture, "alice");

        var settings = (DiscoverRequestSettingsView)((PartialViewResult)await page.OnGetEditAsync(request.Id, CancellationToken.None)).Model!;
        Assert.AreEqual(VideoRequestScope.Custom, settings.SavedScope!.Scope);
        Assert.AreEqual(2, settings.Seasons.Count);

        await page.OnPostEditAsync(request.Id, new DiscoverRequestForm { Scope = "custom", SeasonIds = [second.Id], MonitorFuture = false }, CancellationToken.None);

        var stored = VideoRequestPayload.Parse((await fixture.Store.GetAsync(request.Id, CancellationToken.None))!.PayloadJson)!;
        CollectionAssert.AreEqual(new[] { second.Id }, stored.SelectedSeasonIds);
        Assert.IsFalse(stored.MonitorFuture);

        var foreignSeason = new DiscoverRequestForm { Scope = "custom", SeasonIds = [Guid.NewGuid()] };
        Assert.IsInstanceOfType<BadRequestResult>(await page.OnPostEditAsync(request.Id, foreignSeason, CancellationToken.None), "A season of another title is refused.");
    }

    private static DetailsModel Page(AcquisitionAccessFixture fixture, string profileId, params IAcquisitionRequestExecutor[] executors)
    {
        var page = new DetailsModel(
            fixture.Db,
            AcquisitionAccessFixture.Account(profileId, AccountRole.User),
            fixture.StatusQuery(),
            fixture.Service(profileId, AccountRole.User, executors),
            new VideoRequestScopeResolver(fixture.Db),
            TimeProvider.System)
        {
            MetadataProvider = new EmptyModelMetadataProvider(),
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { User = AcquisitionAccessFixture.Principal(profileId, AccountRole.User) },
                ViewData = new ViewDataDictionary<DetailsModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            }
        };
        return page;
    }

    private static AcquisitionRequestDraft Book(string id) => new(MediaAcquisitionKind.Book, "test", id, id.ToUpperInvariant(), "Author", null);

    private static AcquisitionRequestDraft Anime(string id, AcquisitionRequestOptions? options = null) => new(MediaAcquisitionKind.Anime, "anilist", id, $"Title {id}", null, null, options?.ToPayloadJson());
}
