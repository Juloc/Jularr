using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// What a profile sees of its own requests (docs/mockups/request-status-details): the one consumer projection for lists and the status
/// surface, the poster through the canonical artwork owner, the milestones the stored request still knows, and the consumer words.
/// </summary>
[TestClass]
public sealed class RequestStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task AProfileReadsItsOwnRequestAndNobodyElsesNotEvenAnOwnersLookup()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var mine = await fixture.Store.CreateAsync(Book("mine"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var theirs = await fixture.Store.CreateAsync(Book("theirs"), "bob", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var query = fixture.StatusQuery();

        Assert.IsNotNull(await query.GetOwnAsync(mine.Id, "alice", CancellationToken.None));
        Assert.IsNull(await query.GetOwnAsync(theirs.Id, "alice", CancellationToken.None), "Another profile's request does not exist here.");
        Assert.IsNull(await query.GetOwnAsync(mine.Id, "owner", CancellationToken.None), "The owner reads requests of others in Admin, not on the consumer surface.");
        Assert.IsNull(await query.GetOwnAsync(Guid.NewGuid(), "alice", CancellationToken.None));
    }

    [TestMethod]
    public async Task EveryRequestOfAPageIsProjectedInOneBatchAndMatchesTheSingleProjection()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var withFile = await seed.AddWorkAsync(WorkMediaType.Movie, "Has A File", 2020);
        await seed.AddVideoAsync(withFile, null);
        var withoutFile = await seed.AddWorkAsync(WorkMediaType.Movie, "No File", 2021);
        await LinkAsync(fixture, withFile, "tmdb", "1");
        await LinkAsync(fixture, withoutFile, "tmdb", "2");
        var requests = new[]
        {
            await fixture.Store.CreateAsync(Movie("1", withFile), "alice", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None),
            await fixture.Store.CreateAsync(Movie("2", withoutFile), "alice", AcquisitionRequestStatus.Searching, "owner", CancellationToken.None),
            await fixture.Store.CreateAsync(Book("waiting"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None),
            await fixture.Store.CreateAsync(Book("imported"), "alice", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None),
            await fixture.Store.CreateAsync(Book("failed"), "alice", AcquisitionRequestStatus.Failed, "owner", CancellationToken.None)
        };
        var projection = new ConsumerAcquisitionQuery(fixture.Db, fixture.Store, new VideoRequestWorkResolver(fixture.Db), TimeProvider.System);

        var rows = await fixture.StatusQuery().RowsAsync(requests, "alice", CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { ConsumerAcquisitionState.ReadyToWatch, ConsumerAcquisitionState.LookingForMedia, ConsumerAcquisitionState.WaitingForApproval, ConsumerAcquisitionState.Available, ConsumerAcquisitionState.NeedsAttention },
            rows.Select(row => row.State.State).ToArray());
        foreach (var row in rows)
        {
            Assert.AreEqual(row.State, await projection.ProjectAsync(row.Request, null, playbackEnabled: true, CancellationToken.None), row.Request.ExternalId);
        }
    }

    [TestMethod]
    public async Task ThePosterOfAMovieOrSeriesIsItsCanonicalWorkArtworkAndNeverTheCoverTheRequestCarried()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = await new LibraryCanonicalSeed(fixture.Db).AddWorkAsync(WorkMediaType.Movie, "Salt Flats", 2020);
        await LinkAsync(fixture, work, "tmdb", "410");
        await RequestArtworkSeed.AddPosterAsync(fixture.Db, work.Id);
        var hotlinked = Movie("410", work) with { CoverImageUrl = "https://image.tmdb.org/t/p/w185/hotlink.jpg" };
        var withCover = await fixture.Store.CreateAsync(hotlinked, "alice", AcquisitionRequestStatus.Searching, "owner", CancellationToken.None);
        var unresolved = new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", "999", "Unresolved", null, "https://image.tmdb.org/t/p/w185/other.jpg");
        var noWorkYet = await fixture.Store.CreateAsync(unresolved, "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var mangaCard = new AcquisitionRequestDraft(MediaAcquisitionKind.Manga, "anilist", "5", "A Manga", null, "https://s4.anilist.co/cover.jpg");
        var manga = await fixture.Store.CreateAsync(mangaCard, "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);

        var posters = await new RequestArtworkResolver(fixture.Db, new VideoRequestWorkResolver(fixture.Db)).ResolvePostersAsync([withCover, noWorkYet, manga], "alice", CancellationToken.None);

        Assert.AreEqual($"/works/{work.Id:D}/artwork/", posters[withCover.Id][..($"/works/{work.Id:D}/artwork/".Length)]);
        Assert.IsFalse(posters.ContainsKey(noWorkYet.Id), "A Movie without a Work has no artwork owner yet; the provider cover is not copied in its place.");
        Assert.AreEqual("https://s4.anilist.co/cover.jpg", posters[manga.Id], "A media type without a local artwork owner keeps the cover of its card.");
    }

    [TestMethod]
    public async Task AWatchRequestWithoutAnyCoverStillGetsThePosterOfItsWork()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = await new LibraryCanonicalSeed(fixture.Db).AddWorkAsync(WorkMediaType.Movie, "Watch Now", 2024);
        await LinkAsync(fixture, work, "tmdb", "77");
        await RequestArtworkSeed.AddPosterAsync(fixture.Db, work.Id);
        var instant = await fixture.Store.CreateAsync(Movie("77", work), "alice", AcquisitionRequestStatus.Approved, "alice", CancellationToken.None);
        Assert.IsNull(instant.CoverImageUrl);

        var view = await fixture.StatusQuery().GetOwnAsync(instant.Id, "alice", CancellationToken.None);

        StringAssert.StartsWith(view!.PosterUrl, $"/works/{work.Id:D}/artwork/");
    }

    [TestMethod]
    public async Task WhenTheAcquisitionCannotBeReadTheStatusStillShowsTheSavedRequestAndSaysSo()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var operationId = Guid.NewGuid().ToString("D");
        await fixture.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Operations" ("Id", "Kind", "Category", "Lane", "Status", "Title", "IsDownload", "Attempt", "Retryable", "CreatedAtUtc", "UpdatedAtUtc", "Priority")
            VALUES ({operationId}, 'download', 'acquisition', 1, 2, 'T', 1, 1, 1, 'not-a-date', 'not-a-date', 2)
            """);
        var request = await fixture.Store.CreateAsync(Book("dune"), "alice", AcquisitionRequestStatus.Downloading, "owner", CancellationToken.None);
        await fixture.Store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Downloading, null, Guid.Parse(operationId), null, null, CancellationToken.None);

        var view = await fixture.StatusQuery().GetOwnAsync(request.Id, "alice", CancellationToken.None);

        Assert.IsTrue(view!.StatusUnavailable);
        Assert.AreEqual(request.Id, view.Request.Id);
        Assert.IsNull(view.State.ProgressPercent, "No progress is shown for a state that could not be read.");
        Assert.IsNull(view.OpenUrl);
    }

    [TestMethod]
    public async Task ASeriesThatKeepsWatchingShowsItsNextKnownReleaseOnlyOnceTheRequestIsApproved()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var seed = new LibraryCanonicalSeed(fixture.Db);
        var work = await seed.AddWorkAsync(WorkMediaType.Series, "Salt and Cedar", 2025);
        await LinkAsync(fixture, work, "tmdb", "504");
        var clock = new ManualClock(new DateTimeOffset(Now));
        foreach (var (number, aired) in new[] { (1, Now.AddDays(-20)), (2, Now.AddDays(9)), (3, Now.AddDays(16)) })
        {
            var episode = await seed.AddEpisodeAsync(work, 1, number);
            episode.AiredAt = aired;
        }

        await fixture.Db.SaveChangesAsync();
        var payload = new VideoRequestPayload(work.Id, work.CanonicalTitle, 2025, VideoRequestScope.AllCurrentAndFuture, [], true) { NextSearchUtc = Now.AddDays(1) };
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Tv, "tmdb", "504", work.CanonicalTitle, null, null, payload.Serialize());
        var request = await fixture.Store.CreateAsync(draft, "alice", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);

        var view = await fixture.StatusQuery(clock).GetOwnAsync(request.Id, "alice", CancellationToken.None);

        Assert.AreEqual(ConsumerAcquisitionState.MonitoringFutureReleases, view!.State.State);
        Assert.AreEqual(Now.AddDays(9), view.NextReleaseUtc);
    }

    [TestMethod]
    public void TheTimelineHoldsOnlyTheMilestonesTheStoredRequestStillKnows()
    {
        var created = Now.AddDays(-2);
        var decided = created.AddMinutes(1);
        var changed = Now.AddHours(-3);
        AcquisitionRequest Make(AcquisitionRequestStatus status, string? message = null, DateTime? decidedAt = null, DateTime? updated = null) =>
            new(Guid.NewGuid(), MediaAcquisitionKind.Movie, "tmdb", "1", "T", null, null, null, "alice", status, message, null, null, created, updated ?? created, decidedAt is null ? null : "owner", decidedAt);
        static IReadOnlyList<RequestTimelineMilestone> Of(IReadOnlyList<RequestTimelineEntry> entries) => [.. entries.Select(entry => entry.Milestone)];

        CollectionAssert.AreEqual(new[] { RequestTimelineMilestone.Requested }, Of(RequestStatusQuery.Timeline(Make(AcquisitionRequestStatus.Pending), ConsumerAcquisitionState.WaitingForApproval)).ToArray());
        CollectionAssert.AreEqual(
            new[] { RequestTimelineMilestone.Requested, RequestTimelineMilestone.Approved, RequestTimelineMilestone.GettingMedia },
            Of(RequestStatusQuery.Timeline(Make(AcquisitionRequestStatus.Downloading, null, decided, changed), ConsumerAcquisitionState.GettingMedia)).ToArray());
        CollectionAssert.AreEqual(
            new[] { RequestTimelineMilestone.Requested, RequestTimelineMilestone.Approved },
            Of(RequestStatusQuery.Timeline(Make(AcquisitionRequestStatus.Approved, null, decided, decided), ConsumerAcquisitionState.LookingForMedia)).ToArray(),
            "A request that did not change since its approval has no later stage to list.");
        CollectionAssert.AreEqual(
            new[] { RequestTimelineMilestone.Requested, RequestTimelineMilestone.Rejected },
            Of(RequestStatusQuery.Timeline(Make(AcquisitionRequestStatus.Rejected, "private", decided, changed), ConsumerAcquisitionState.Rejected)).ToArray());
        var cancelled = RequestStatusQuery.Timeline(Make(AcquisitionRequestStatus.Rejected, AcquisitionRequest.CancelledMessage, null, changed), ConsumerAcquisitionState.Rejected);
        CollectionAssert.AreEqual(new[] { RequestTimelineMilestone.Requested, RequestTimelineMilestone.Cancelled }, Of(cancelled).ToArray());
        Assert.AreEqual(changed, cancelled[1].AtUtc);
    }

    [TestMethod]
    public void TheConsumerWordsKeepOneLabelAndOneToneForOneState()
    {
        var ui = UiTextBundle.English;
        AcquisitionRequest Make(AcquisitionRequestStatus status, string? message = null) =>
            new(Guid.NewGuid(), MediaAcquisitionKind.Movie, "tmdb", "1", "T", null, null, null, "alice", status, message, null, null, Now, Now, null, null);
        ConsumerAcquisitionView View(ConsumerAcquisitionState state, int? percent = null) => new(state, ConsumerMediaUnit.Movie, percent, false);

        var approved = Make(AcquisitionRequestStatus.Approved);
        var searching = Make(AcquisitionRequestStatus.Searching);
        Assert.AreEqual(RequestStatusText.StateLabel(ui, approved, View(ConsumerAcquisitionState.LookingForMedia)), RequestStatusText.StateLabel(ui, searching, View(ConsumerAcquisitionState.LookingForMedia)));
        var looking = View(ConsumerAcquisitionState.LookingForMedia);
        Assert.AreEqual(RequestStatusText.Tone(approved, looking), RequestStatusText.Tone(searching, looking), "Approved and searching are both \"Looking for media\" and look alike.");

        Assert.AreEqual("Getting movie · 32%", RequestStatusText.StateLabel(ui, approved, View(ConsumerAcquisitionState.GettingMedia, 32)));
        var failed = Make(AcquisitionRequestStatus.Failed);
        Assert.AreEqual("Needs attention", RequestStatusText.StateLabel(ui, failed, View(ConsumerAcquisitionState.NeedsAttention)));
        Assert.AreEqual("Could not complete request", RequestStatusText.Headline(ui, failed, View(ConsumerAcquisitionState.NeedsAttention)));
        var cancelled = Make(AcquisitionRequestStatus.Rejected, AcquisitionRequest.CancelledMessage);
        Assert.AreEqual("Request cancelled", RequestStatusText.StateLabel(ui, cancelled, View(ConsumerAcquisitionState.Rejected)));
        Assert.AreEqual(RequestStateTone.Neutral, RequestStatusText.Tone(cancelled, View(ConsumerAcquisitionState.Rejected)));
        Assert.AreEqual("Request rejected", RequestStatusText.StateLabel(ui, Make(AcquisitionRequestStatus.Rejected, "Not suitable."), View(ConsumerAcquisitionState.Rejected)));
        Assert.IsNull(RequestStatusText.Hint(ui, approved, View(ConsumerAcquisitionState.MonitoringFutureReleases)), "Monitoring is told by the next release card, not a sentence.");
        var rejectedHint = RequestStatusText.Hint(ui, Make(AcquisitionRequestStatus.Rejected, "Not suitable."), View(ConsumerAcquisitionState.Rejected));
        Assert.IsFalse(rejectedHint!.Contains("Not suitable", StringComparison.Ordinal), "A rejection reason is private until it is explicitly shared.");
    }

    [TestMethod]
    public void TimesAreRelativeForAWeekAndADateAfterwards()
    {
        var ui = UiTextBundle.English;

        Assert.AreEqual("Just now", RequestStatusText.Age(ui, Now.AddSeconds(-20), Now));
        Assert.AreEqual("5 min ago", RequestStatusText.Age(ui, Now.AddMinutes(-5), Now));
        Assert.AreEqual("3 h ago", RequestStatusText.Age(ui, Now.AddHours(-3), Now));
        Assert.AreEqual("2 d ago", RequestStatusText.Age(ui, Now.AddDays(-2), Now));
        Assert.AreEqual("26 Sep 2026", RequestStatusText.Age(ui, Now.AddDays(-10), Now));
        Assert.AreEqual("6 Oct 12:00 UTC", RequestStatusText.Stamp(new UiTextBundle("en-GB", "ltr", new Dictionary<string, string>()), Now));
    }

    [TestMethod]
    public void TheSavedIntentIsReadBackInTheWordsOfTheRequestDialog()
    {
        var ui = UiTextBundle.English;
        var series = new VideoRequestPayload(Guid.NewGuid(), "T", 2022, VideoRequestScope.FutureOnly, [], true);
        var tv = new AcquisitionRequest(Guid.NewGuid(), MediaAcquisitionKind.Tv, "tmdb", "1", "T", null, null, series.Serialize(), "alice", AcquisitionRequestStatus.Pending, null, null, null, Now, Now, null, null);
        var options = new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [1, 2], AudioLanguage = "ja", SubtitleLanguage = "off", QualityProfileId = "profile" };
        var anime = tv with { Kind = MediaAcquisitionKind.Anime, PayloadJson = options.ToPayloadJson() };
        var movie = tv with { Kind = MediaAcquisitionKind.Movie, PayloadJson = null };

        CollectionAssert.AreEqual(new[] { ("Scope", "Future only") }, RequestStatusText.Intent(ui, tv).ToArray());
        var rows = RequestStatusText.Intent(ui, anime);
        CollectionAssert.AreEqual(new[] { "Scope", "Seasons", "Audio", "Subtitles" }, rows.Select(row => row.Label).ToArray());
        Assert.IsFalse(rows.Any(row => row.Value.Contains("profile", StringComparison.Ordinal)), "A quality profile is not a consumer choice.");
        Assert.AreEqual(0, RequestStatusText.Intent(ui, movie).Count);
        Assert.AreEqual("Movie · 2022", RequestStatusText.Identity(ui, movie with { PayloadJson = series.Serialize() }));
    }

    private static async Task LinkAsync(AcquisitionAccessFixture fixture, Work work, string provider, string externalId)
    {
        fixture.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = work.MediaType, Provider = provider, ExternalId = externalId, IsPrimary = true });
        await fixture.Db.SaveChangesAsync();
    }

    private static AcquisitionRequestDraft Movie(string tmdbId, Work work) =>
        new(MediaAcquisitionKind.Movie, "tmdb", tmdbId, work.CanonicalTitle, null, null, new VideoRequestPayload(work.Id, work.CanonicalTitle, work.Year, VideoRequestScope.WholeWork, [], false).Serialize());

    private static AcquisitionRequestDraft Book(string id) => new(MediaAcquisitionKind.Book, "test", id, id, "Author", null);
}
