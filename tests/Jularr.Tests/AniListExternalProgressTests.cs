using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Pages.Library;
using Jularr.Web.Pages.Manga;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class AniListExternalProgressTests
{
    private const string Owner = "owner";
    private const string Learner = "learner-1";

    [TestMethod]
    public async Task MangaStatesResolveThroughCanonicalContextAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        var seriesId = await fixture.AddMangaAsync("Example Manga", "321", chapter: 5, readToEnd: true);
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");

        var cases = new (FakeAniList Remote, AniListExternalProgressStateKind Expected)[]
        {
            (FakeAniList.OnList(progress: 5, chapters: 100), AniListExternalProgressStateKind.Synced),
            (FakeAniList.OnList(progress: 3, chapters: 100), AniListExternalProgressStateKind.LocalAhead),
            (FakeAniList.OnList(progress: 9, chapters: 100), AniListExternalProgressStateKind.AniListAhead),
            (FakeAniList.NotOnList(), AniListExternalProgressStateKind.NotOnList),
            (FakeAniList.Outage(), AniListExternalProgressStateKind.RemoteUnavailable),
            (FakeAniList.ServerError(), AniListExternalProgressStateKind.RemoteUnavailable)
        };

        foreach (var (remote, expected) in cases)
        {
            var state = await fixture.Service(Owner, remote)
                .GetMangaProgressStateAsync(seriesId, CancellationToken.None);

            Assert.AreEqual(expected, state.Kind, $"remote {remote.Name}");
            Assert.AreEqual(5, state.LocalProgress);
            Assert.AreEqual(321, state.MediaId);
        }

        var localAhead = await fixture.Service(Owner, FakeAniList.OnList(progress: 3, chapters: 100))
            .GetMangaProgressStateAsync(seriesId, CancellationToken.None);
        Assert.IsTrue(localAhead.CanSync);
        Assert.AreEqual(3, localAhead.RemoteProgress);
        Assert.AreEqual("CURRENT", localAhead.RemoteStatus);

        var remoteAhead = await fixture.Service(Owner, FakeAniList.OnList(progress: 9, chapters: 100))
            .GetMangaProgressStateAsync(seriesId, CancellationToken.None);
        Assert.IsFalse(remoteAhead.CanSync, "AniList ahead must never enable a backward write.");
    }

    [TestMethod]
    public async Task MangaLocalOnlyStatesDoNotContactAniListAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        var matched = await fixture.AddMangaAsync("Matched Manga", "321", chapter: 5, readToEnd: true);
        var unmatched = await fixture.AddMangaAsync("Loose Manga", null, chapter: 2, readToEnd: true);
        var unread = await fixture.AddMangaAsync("Unread Manga", "654", chapter: 1, readToEnd: null);
        var reviewed = await fixture.AddMangaAsync("Reviewed Manga", "987", chapter: 4, readToEnd: true);
        await fixture.ReviewStore.UpsertAsync(
            "manga",
            reviewed.ToString(),
            "Reviewed Manga",
            "reading-segments",
            "Ambiguous relation chain.",
            []);

        var remote = FakeAniList.FailIfCalled();
        var service = fixture.Service(Owner, remote);

        Assert.AreEqual(
            AniListExternalProgressStateKind.NotConnected,
            (await service.GetMangaProgressStateAsync(matched, CancellationToken.None)).Kind);
        Assert.AreEqual(
            AniListExternalProgressStateKind.MappingNeedsReview,
            (await service.GetMangaProgressStateAsync(unmatched, CancellationToken.None)).Kind);
        Assert.AreEqual(
            AniListExternalProgressStateKind.NoLocalProgress,
            (await service.GetMangaProgressStateAsync(unread, CancellationToken.None)).Kind);

        var review = await service.GetMangaProgressStateAsync(reviewed, CancellationToken.None);
        Assert.AreEqual(AniListExternalProgressStateKind.MappingNeedsReview, review.Kind);
        Assert.AreEqual("Ambiguous relation chain.", review.Message);
        Assert.IsFalse(review.CanSync);

        Assert.AreEqual(0, remote.Calls);
    }

    [TestMethod]
    public async Task NovelStatesResolveThroughCanonicalContextAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        var workId = await fixture.AddNovelAsync("Example Novel", "777", chapter: 6, permille: 1000);
        var unread = await fixture.AddNovelAsync("Unread Novel", "778", chapter: null, permille: 0);

        var notConnected = await fixture.Service(Owner, FakeAniList.FailIfCalled())
            .GetNovelProgressStateAsync(workId, CancellationToken.None);
        Assert.AreEqual(AniListExternalProgressStateKind.NotConnected, notConnected.Kind);
        Assert.AreEqual(6, notConnected.LocalProgress);

        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");

        var cases = new (FakeAniList Remote, AniListExternalProgressStateKind Expected)[]
        {
            (FakeAniList.OnList(progress: 6, chapters: 50), AniListExternalProgressStateKind.Synced),
            (FakeAniList.OnList(progress: 2, chapters: 50), AniListExternalProgressStateKind.LocalAhead),
            (FakeAniList.OnList(progress: 12, chapters: 50), AniListExternalProgressStateKind.AniListAhead),
            (FakeAniList.NotOnList(), AniListExternalProgressStateKind.NotOnList),
            (FakeAniList.Outage(), AniListExternalProgressStateKind.RemoteUnavailable)
        };

        foreach (var (remote, expected) in cases)
        {
            var state = await fixture.Service(Owner, remote)
                .GetNovelProgressStateAsync(workId, CancellationToken.None);
            Assert.AreEqual(expected, state.Kind, $"remote {remote.Name}");
            Assert.AreEqual(777, state.MediaId);
        }

        Assert.AreEqual(
            AniListExternalProgressStateKind.NoLocalProgress,
            (await fixture.Service(Owner, FakeAniList.FailIfCalled())
                .GetNovelProgressStateAsync(unread, CancellationToken.None)).Kind);

        await fixture.ReviewStore.UpsertAsync(
            "novel",
            workId.ToString(),
            "Example Novel",
            "identity",
            "Two AniList entries match equally well.",
            []);
        Assert.AreEqual(
            AniListExternalProgressStateKind.MappingNeedsReview,
            (await fixture.Service(Owner, FakeAniList.FailIfCalled())
                .GetNovelProgressStateAsync(workId, CancellationToken.None)).Kind);
    }

    [TestMethod]
    public async Task AnimeStatesUseTheContiguouslyCompletedEpisodeAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 12);
        await fixture.MarkWatchedAsync(Owner, anime, 1, 2, 4);

        Assert.AreEqual(
            AniListExternalProgressStateKind.NotConnected,
            (await fixture.Service(Owner, FakeAniList.FailIfCalled())
                .GetAnimeProgressStateAsync(anime.Id, CancellationToken.None)).Kind);

        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");

        var cases = new (FakeAniList Remote, AniListExternalProgressStateKind Expected)[]
        {
            (FakeAniList.OnList(progress: 2), AniListExternalProgressStateKind.Synced),
            (FakeAniList.OnList(progress: 1), AniListExternalProgressStateKind.LocalAhead),
            (FakeAniList.OnList(progress: 7), AniListExternalProgressStateKind.AniListAhead),
            (FakeAniList.NotOnList(), AniListExternalProgressStateKind.NotOnList),
            (FakeAniList.Outage(), AniListExternalProgressStateKind.RemoteUnavailable)
        };

        foreach (var (remote, expected) in cases)
        {
            var state = await fixture.Service(Owner, remote)
                .GetAnimeProgressStateAsync(anime.Id, CancellationToken.None);
            Assert.AreEqual(expected, state.Kind, $"remote {remote.Name}");
            Assert.AreEqual(2, state.LocalProgress, "E1, E2 and E4 finished with E3 open: the state stops at the gap.");
            Assert.AreEqual(555, state.MediaId);
        }

        var episodeState = await fixture.Service(Owner, FakeAniList.OnList(progress: 1))
            .GetEpisodeProgressStateAsync(anime.Episodes[1], CancellationToken.None);
        Assert.AreEqual(AniListExternalProgressStateKind.LocalAhead, episodeState.Kind);
        Assert.AreEqual(2, episodeState.LocalProgress);

        var afterGap = await fixture.Service(Owner, FakeAniList.OnList(progress: 1))
            .GetEpisodeProgressStateAsync(anime.Episodes[3], CancellationToken.None);
        Assert.AreEqual(AniListExternalProgressStateKind.NoLocalProgress, afterGap.Kind, "A finished episode behind a gap is not written.");

        var unfinished = await fixture.Service(Owner, FakeAniList.OnList(progress: 1))
            .GetEpisodeProgressStateAsync(anime.Episodes[7], CancellationToken.None);
        Assert.AreEqual(AniListExternalProgressStateKind.NoLocalProgress, unfinished.Kind, "An episode that was never finished is not written.");
    }

    [TestMethod]
    public async Task AnimeLocalOnlyStatesDoNotContactAniListAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        var unwatched = await fixture.AddAnimeAsync("Unwatched Anime", "555", episodes: 3);
        var unmatched = await fixture.AddAnimeAsync("Unmatched Anime", null, episodes: 3);
        await fixture.MarkWatchedAsync(Owner, unmatched, 1);
        var reviewed = await fixture.AddAnimeAsync("Reviewed Anime", "556", episodes: 3);
        await fixture.MarkWatchedAsync(Owner, reviewed, 1);
        await fixture.ReviewStore.UpsertAsync(
            "anime",
            reviewed.Id.ToString(),
            "Reviewed Anime",
            "episode-ranges",
            "Special relation is ambiguous.",
            []);
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");

        var remote = FakeAniList.FailIfCalled();
        var service = fixture.Service(Owner, remote);

        Assert.AreEqual(
            AniListExternalProgressStateKind.NoLocalProgress,
            (await service.GetAnimeProgressStateAsync(unwatched.Id, CancellationToken.None)).Kind);
        Assert.AreEqual(
            AniListExternalProgressStateKind.MappingNeedsReview,
            (await service.GetAnimeProgressStateAsync(unmatched.Id, CancellationToken.None)).Kind);
        Assert.AreEqual(
            AniListExternalProgressStateKind.MappingNeedsReview,
            (await service.GetAnimeProgressStateAsync(reviewed.Id, CancellationToken.None)).Kind);
        Assert.AreEqual(0, remote.Calls);
    }

    [TestMethod]
    public async Task ProfilesNeverSeeEachOthersAniListStateAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        var seriesId = await fixture.AddMangaAsync("Shared Manga", "321", chapter: 5, readToEnd: true);
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");

        var learnerRemote = FakeAniList.FailIfCalled();
        var learnerState = await fixture.Service(Learner, learnerRemote)
            .GetMangaProgressStateAsync(seriesId, CancellationToken.None);

        Assert.AreEqual(AniListExternalProgressStateKind.NoLocalProgress, learnerState.Kind);
        Assert.AreEqual(0, learnerRemote.Calls);

        await fixture.AddMangaProgressAsync(Learner, seriesId, readToEnd: true);
        learnerState = await fixture.Service(Learner, learnerRemote)
            .GetMangaProgressStateAsync(seriesId, CancellationToken.None);
        Assert.AreEqual(
            AniListExternalProgressStateKind.NotConnected,
            learnerState.Kind,
            "The owner's AniList connection must not leak into another profile.");
        Assert.AreEqual(0, learnerRemote.Calls);

        await fixture.ConnectAsync(Learner, viewerId: 77, "learner-token");
        var ownerRemote = FakeAniList.OnList(progress: 5, chapters: 100);
        var learnerConnected = FakeAniList.OnList(progress: 1, chapters: 100);

        var owner = await fixture.Service(Owner, ownerRemote)
            .GetMangaProgressStateAsync(seriesId, CancellationToken.None);
        var learner = await fixture.Service(Learner, learnerConnected)
            .GetMangaProgressStateAsync(seriesId, CancellationToken.None);

        Assert.AreEqual(AniListExternalProgressStateKind.Synced, owner.Kind);
        Assert.AreEqual(AniListExternalProgressStateKind.LocalAhead, learner.Kind);
        CollectionAssert.AreEquivalent(new[] { "owner-token" }, ownerRemote.Tokens.Distinct().ToArray());
        CollectionAssert.AreEquivalent(new[] { "learner-token" }, learnerConnected.Tokens.Distinct().ToArray());
        Assert.IsTrue(ownerRemote.Bodies.Where(x => x.Contains("MediaList")).All(x => x.Contains("\"userId\":42")));
        Assert.IsTrue(learnerConnected.Bodies.Where(x => x.Contains("MediaList")).All(x => x.Contains("\"userId\":77")));
    }

    [TestMethod]
    public async Task SummariesAreLocalOnlyAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");
        var seriesId = await fixture.AddMangaAsync("Example Manga", "321", chapter: 5, readToEnd: false);
        var workId = await fixture.AddNovelAsync("Example Novel", "777", chapter: 3, permille: 400);
        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 12);
        await fixture.MarkWatchedAsync(Owner, anime, 1, 2, 3);

        var remote = FakeAniList.FailIfCalled();
        var service = fixture.Service(Owner, remote);

        var manga = await service.GetMangaProgressSummaryAsync(seriesId, CancellationToken.None);
        var novel = await service.GetNovelProgressSummaryAsync(workId, CancellationToken.None);
        var animeSummary = await service.GetAnimeProgressSummaryAsync(anime.Id, CancellationToken.None);
        var episode = await service.GetEpisodeProgressSummaryAsync(anime.Episodes[2], CancellationToken.None);

        Assert.IsNotNull(manga);
        Assert.AreEqual("Chapter 5 · page 3 of 10", manga.LocalProgressText);
        Assert.AreEqual(321, manga.MatchedMediaId);
        Assert.AreEqual(ExternalMappingBasis.Matched, manga.MappingBasis);
        Assert.AreEqual("https://anilist.co/manga/321", manga.AniListUrl);

        Assert.IsNotNull(novel);
        Assert.AreEqual("Chapter 3 · 40% read", novel.LocalProgressText);
        Assert.AreEqual(777, novel.MatchedMediaId);

        Assert.IsNotNull(animeSummary);
        Assert.AreEqual("3 of 12 episodes watched · latest S01E03", animeSummary.LocalProgressText);
        Assert.AreEqual(555, animeSummary.MatchedMediaId);
        Assert.AreEqual("https://anilist.co/anime/555", animeSummary.AniListUrl);

        Assert.IsNotNull(episode);
        Assert.AreEqual("S01E03 watched", episode.LocalProgressText);

        Assert.AreEqual(0, remote.Calls);
    }

    [TestMethod]
    public async Task SummaryReportsPendingReviewAndMissingMatchAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        var unmatched = await fixture.AddMangaAsync("Loose Manga", null, chapter: 1, readToEnd: null);
        await fixture.ReviewStore.UpsertAsync(
            "manga",
            unmatched.ToString(),
            "Loose Manga",
            "identity",
            "Two candidates are equally likely.",
            []);

        var summary = await fixture.Service(Owner, FakeAniList.FailIfCalled())
            .GetMangaProgressSummaryAsync(unmatched, CancellationToken.None);

        Assert.IsNotNull(summary);
        Assert.IsFalse(summary.IsMatched);
        Assert.IsFalse(summary.HasLocalProgress);
        Assert.AreEqual("Not started", summary.LocalProgressText);
        Assert.AreEqual(ExternalMappingBasis.NeedsReview, summary.MappingBasis);
        Assert.AreEqual("Two candidates are equally likely.", summary.ReviewReason);
    }

    [TestMethod]
    public async Task MangaPageGetRendersLocalProgressWithoutAniListAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");
        var seriesId = await fixture.AddMangaAsync("Example Manga", "321", chapter: 5, readToEnd: true);
        var remote = FakeAniList.FailIfCalled();
        var page = fixture.SeriesPage(Owner, remote);

        var result = await page.OnGetAsync(seriesId, null, CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsNotNull(page.ExternalProgress);
        Assert.AreEqual("Chapter 5 · page 10 of 10", page.ExternalProgress.LocalProgressText);
        Assert.AreEqual(0, remote.Calls, "A normal page GET must never wait on AniList.");
    }

    [TestMethod]
    public async Task AnimePageGetRendersLocalProgressWithoutAniListAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");
        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 4);
        await fixture.MarkWatchedAsync(Owner, anime, 1, 2);
        var remote = FakeAniList.FailIfCalled();
        var page = fixture.AnimePage(Owner, remote);

        var result = await page.OnGetAsync(anime.Id, null, null, null, null, CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsNotNull(page.ExternalProgress);
        Assert.AreEqual("2 of 4 episodes watched · latest S01E02", page.ExternalProgress.LocalProgressText);
        Assert.AreEqual(0, remote.Calls, "A normal page GET must never wait on AniList.");
    }

    [TestMethod]
    public async Task LazyEndpointReturnsCanonicalStateAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");
        var seriesId = await fixture.AddMangaAsync("Example Manga", "321", chapter: 5, readToEnd: true);

        var expected = await fixture.Service(Owner, FakeAniList.OnList(progress: 3, chapters: 100))
            .GetMangaProgressStateAsync(seriesId, CancellationToken.None);
        var page = fixture.SeriesPage(Owner, FakeAniList.OnList(progress: 3, chapters: 100));

        var result = await page.OnGetExternalProgressAsync(seriesId, CancellationToken.None);

        var partial = Assert.IsInstanceOfType<PartialViewResult>(result);
        Assert.AreEqual("_ExternalProgressState", partial.ViewName);
        var view = Assert.IsInstanceOfType<ExternalProgressRemoteView>(partial.Model);
        Assert.AreEqual(expected, view.State);
        Assert.AreEqual(AniListExternalProgressStateKind.LocalAhead, view.State.Kind);
        Assert.AreEqual("SyncAniList", view.SyncHandler);
        Assert.AreEqual("no-store", page.Response.Headers.CacheControl.ToString());

        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 4);
        await fixture.MarkWatchedAsync(Owner, anime, 1, 2);
        var animePage = fixture.AnimePage(Owner, FakeAniList.OnList(progress: 2));
        var animeResult = await animePage.OnGetExternalProgressAsync(anime.Id, CancellationToken.None);
        var animeView = Assert.IsInstanceOfType<ExternalProgressRemoteView>(
            Assert.IsInstanceOfType<PartialViewResult>(animeResult).Model);
        Assert.AreEqual(AniListExternalProgressStateKind.Synced, animeView.State.Kind);

        Assert.IsInstanceOfType<NotFoundResult>(
            await fixture.SeriesPage(Owner, FakeAniList.FailIfCalled())
                .OnGetExternalProgressAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.IsInstanceOfType<NotFoundResult>(
            await fixture.AnimePage(Owner, FakeAniList.FailIfCalled())
                .OnGetExternalProgressAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task LazyEndpointDegradesWhenAniListFailsAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");
        var seriesId = await fixture.AddMangaAsync("Example Manga", "321", chapter: 5, readToEnd: true);
        var anime = await fixture.AddAnimeAsync("Example Anime", "555", episodes: 4);
        await fixture.MarkWatchedAsync(Owner, anime, 1);

        foreach (var remote in new[] { FakeAniList.Outage(), FakeAniList.ServerError(), FakeAniList.Timeout() })
        {
            var mangaResult = await fixture.SeriesPage(Owner, remote)
                .OnGetExternalProgressAsync(seriesId, CancellationToken.None);
            var mangaView = Assert.IsInstanceOfType<ExternalProgressRemoteView>(
                Assert.IsInstanceOfType<PartialViewResult>(mangaResult).Model);
            Assert.AreEqual(AniListExternalProgressStateKind.RemoteUnavailable, mangaView.State.Kind, remote.Name);
            Assert.IsFalse(mangaView.State.CanSync);
            Assert.AreEqual(5, mangaView.State.LocalProgress, "Local progress survives AniList failures.");

            var animeResult = await fixture.AnimePage(Owner, remote)
                .OnGetExternalProgressAsync(anime.Id, CancellationToken.None);
            var animeView = Assert.IsInstanceOfType<ExternalProgressRemoteView>(
                Assert.IsInstanceOfType<PartialViewResult>(animeResult).Model);
            Assert.AreEqual(AniListExternalProgressStateKind.RemoteUnavailable, animeView.State.Kind, remote.Name);
        }
    }

    [TestMethod]
    public async Task SyncDoesNotWriteWhenAniListIsUnavailableAsync()
    {
        await using var fixture = await ExternalProgressFixture.CreateAsync();
        await fixture.ConnectAsync(Owner, viewerId: 42, "owner-token");
        var seriesId = await fixture.AddMangaAsync("Example Manga", "321", chapter: 5, readToEnd: true);
        var remote = FakeAniList.Outage();

        var result = await fixture.Service(Owner, remote)
            .SyncMangaProgressAsync(seriesId, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.Changed);
        Assert.IsFalse(remote.Bodies.Any(x => x.Contains("SaveMediaListEntry")));
    }

    [TestMethod]
    public void MissingListEntryIsRecognizedOnlyForGraphQlNotFound()
    {
        Assert.IsTrue(AniListAccountService.IsMissingListEntryResponse(
            HttpStatusCode.NotFound,
            FakeAniList.NotFoundBody));
        Assert.IsFalse(AniListAccountService.IsMissingListEntryResponse(
            HttpStatusCode.NotFound,
            "<html>gateway</html>"));
        Assert.IsFalse(AniListAccountService.IsMissingListEntryResponse(
            HttpStatusCode.InternalServerError,
            FakeAniList.NotFoundBody));
    }

    internal sealed class FakeAniList : HttpMessageHandler
    {
        public const string NotFoundBody =
            """{"errors":[{"message":"Not Found.","status":404,"locations":[{"line":2,"column":3}]}],"data":{"MediaList":null}}""";

        private readonly Func<string, HttpResponseMessage> respond;

        private FakeAniList(string name, Func<string, HttpResponseMessage> respond)
        {
            Name = name;
            this.respond = respond;
        }

        public string Name { get; }
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];
        public List<string> Tokens { get; } = [];

        public static FakeAniList OnList(int progress, int? chapters = null) =>
            new($"on-list:{progress}", body => body.Contains("MediaList(")
                ? Json(HttpStatusCode.OK, ListEntry(progress, MediaIdOf(body)))
                : Json(
                    HttpStatusCode.OK,
                    """{"data":{"Media":{"id":321,"chapters":""" +
                    (chapters?.ToString() ?? "null") +
                    "}}}"));

        public static FakeAniList NotOnList() =>
            new("not-on-list", _ => Json(HttpStatusCode.NotFound, NotFoundBody));

        public static FakeAniList ServerError() =>
            new("http-500", _ => Json(HttpStatusCode.InternalServerError, """{"errors":[{"message":"Internal"}]}"""));

        public static FakeAniList Outage() =>
            new("outage", _ => throw new HttpRequestException("connection refused"));

        public static FakeAniList Timeout() =>
            new("timeout", _ => throw new TaskCanceledException("timed out"));

        public static FakeAniList FailIfCalled() =>
            new("fail-if-called", _ => throw new InvalidOperationException(
                "AniList must not be contacted here."));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            Tokens.Add(request.Headers.Authorization?.Parameter ?? "");
            return respond(body);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        private static int MediaIdOf(string body) =>
            int.Parse(Regex.Match(body, "\"mediaId\":(\\d+)").Groups[1].Value);

        private static string ListEntry(int progress, int mediaId) =>
            $$$"""
            {"data":{"MediaList":{
              "id":1,"userId":42,"mediaId":{{{mediaId}}},"status":"CURRENT","progress":{{{progress}}},
              "progressVolumes":0,"score":0,"repeat":0,"priority":0,"private":false,"notes":null,
              "hiddenFromStatusLists":false,"customLists":null,"advancedScores":null,
              "startedAt":{"year":null,"month":null,"day":null},
              "completedAt":{"year":null,"month":null,"day":null},"updatedAt":1} } }
            """;
    }

    private sealed class ExternalProgressFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly AniListAccountStore accountStore;
        private readonly ReadingSegmentMappingStore segmentStore;

        private ExternalProgressFixture(string root, AppDbContext db)
        {
            this.root = root;
            Db = db;
            accountStore = new AniListAccountStore(
                new EphemeralDataProtectionProvider(),
                NullLogger<AniListAccountStore>.Instance,
                new DirectoryInfo(root));
            var mappingDirectory = new DirectoryInfo(Path.Combine(root, "anilist"));
            ReviewStore = new MediaMappingReviewStore(
                NullLogger<MediaMappingReviewStore>.Instance,
                mappingDirectory);
            segmentStore = new ReadingSegmentMappingStore(
                NullLogger<ReadingSegmentMappingStore>.Instance,
                mappingDirectory);
        }

        public AppDbContext Db { get; }
        public MediaMappingReviewStore ReviewStore { get; }

        public static async Task<ExternalProgressFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "jularr-tests",
                $"external-progress-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "anilist"));
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new ExternalProgressFixture(root, db);
        }

        public AniListAccountService Service(string profileId, FakeAniList remote) =>
            new(
                new HttpClient(remote) { BaseAddress = new Uri("https://graphql.anilist.co/") },
                accountStore,
                Db,
                Metadata(),
                segmentStore,
                ReviewStore,
                EpisodeFlowFixture.Account(profileId),
                NullLogger<AniListAccountService>.Instance);

        public SeriesModel SeriesPage(string profileId, FakeAniList remote)
        {
            var page = new SeriesModel(
                Db,
                EpisodeFlowFixture.Account(profileId),
                new SingleClientFactory(remote),
                new OperationRunner(Db, new ServiceCollection().BuildServiceProvider()),
                ReviewStore,
                Service(profileId, remote),
                new FranchiseStore(Db),
                CreateFranchiseService());
            Attach(page);
            return page;
        }

        public AnimeModel AnimePage(string profileId, FakeAniList remote)
        {
            var account = EpisodeFlowFixture.Account(profileId);
            var page = new AnimeModel(
                Db,
                Metadata(),
                account,
                new OperationRunner(Db, new ServiceCollection().BuildServiceProvider()),
                EpisodeFlowFixture.ProgressService(Db, account),
                Service(profileId, remote),
                new FranchiseStore(Db),
                CreateFranchiseService(),
                new WatchlistStore(Db),
                new WatchlistLibraryResolver(Db),
                new Jularr.Web.Features.Acquisition.Access.AcquisitionAccessStore(Db),
                new Jularr.Web.Features.Auth.MediaCapabilityService(
                    new Jularr.Web.Features.Auth.MediaCapabilityStore(
                        Path.Combine(Path.GetTempPath(), $"jularr-anime-page-caps-{Guid.NewGuid():N}"))),
                NullLogger<AnimeModel>.Instance);
            Attach(page);
            return page;
        }

        // These pages only read existing franchise membership (#425); nothing here exercises the
        // AniList-backed refresh, so the source and limiter are unused placeholders.
        private FranchiseService CreateFranchiseService() =>
            new(
                new FranchiseStore(Db),
                new MediaRelationStore(Db),
                new NoopFranchiseRelationSource(),
                new AniListRequestLimiter(new AniListRateLimitGate(), TimeProvider.System),
                new FranchiseRefreshSignal(),
                NullLogger<FranchiseService>.Instance);

        public Task ConnectAsync(string profileId, int viewerId, string token) =>
            accountStore.SaveAsync(
                profileId,
                new StoredAniListAccount(
                    12345,
                    viewerId,
                    $"viewer-{viewerId}",
                    null,
                    token,
                    DateTimeOffset.UtcNow,
                    null),
                CancellationToken.None);

        public async Task<Guid> AddMangaAsync(
            string title,
            string? externalId,
            int chapter,
            bool? readToEnd)
        {
            var repository = new MangaRepository(Db);
            var seriesId = Guid.NewGuid();
            await repository.UpsertSeriesAsync(
                seriesId,
                title,
                Path.Combine(root, "manga", seriesId.ToString("N")),
                CancellationToken.None);

            for (var number = 1; number <= chapter; number++)
            {
                await repository.UpsertChapterAsync(
                    new MangaChapterItem(
                        Guid.NewGuid(),
                        seriesId,
                        number,
                        null,
                        $"Chapter {number}",
                        10,
                        "folder",
                        DateTime.UtcNow),
                    Path.Combine(root, "manga", seriesId.ToString("N"), number.ToString()),
                    CancellationToken.None);
            }

            if (externalId is not null)
            {
                await repository.UpdateMetadataAsync(
                    seriesId,
                    new MangaAniListCandidate(externalId, title, null, null, null, null, null),
                    CancellationToken.None);
            }

            if (readToEnd is bool toEnd)
            {
                await AddMangaProgressAsync(Owner, seriesId, toEnd);
            }

            return seriesId;
        }

        public async Task AddMangaProgressAsync(string profileId, Guid seriesId, bool readToEnd)
        {
            var repository = new MangaRepository(Db);
            var last = (await repository.GetChaptersAsync(seriesId, CancellationToken.None))
                .OrderBy(x => x.Number)
                .Last();
            var chapter = await repository.GetChapterAsync(last.Id, CancellationToken.None);
            await repository.SaveProgressAsync(
                profileId,
                chapter!,
                readToEnd ? chapter!.PageCount - 1 : 2,
                CancellationToken.None);
        }

        public async Task<Guid> AddNovelAsync(
            string title,
            string externalId,
            int? chapter,
            int permille)
        {
            var work = new NovelWork
            {
                SourceProvider = "test",
                SourceKey = Guid.NewGuid().ToString("N"),
                SourceUrl = "https://example.invalid/novel",
                Title = title,
                MetadataProvider = "anilist",
                MetadataExternalId = externalId,
                MetadataTitle = title
            };
            Db.Add(work);
            var volume = new NovelVolume { WorkId = work.Id, Number = 1, SourceKey = "web" };
            Db.Add(volume);

            NovelChapter? current = null;
            for (var number = 1; number <= Math.Max(1, chapter ?? 1); number++)
            {
                current = new NovelChapter
                {
                    WorkId = work.Id,
                    VolumeId = volume.Id,
                    Number = number,
                    SourceUrl = $"https://example.invalid/novel/{number}",
                    Title = $"Chapter {number}",
                    OriginalText = "本文",
                    SourceHash = Guid.NewGuid().ToString("N")
                };
                Db.Add(current);
            }

            if (chapter is not null)
            {
                Db.Add(new NovelProgress
                {
                    ProfileId = Owner,
                    WorkId = work.Id,
                    ChapterId = current!.Id,
                    PositionPermille = permille
                });
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return work.Id;
        }

        public async Task<TestAnime> AddAnimeAsync(string title, string? externalId, int episodes)
        {
            var anime = new Anime { Key = Guid.NewGuid().ToString("N"), Title = title };
            Db.Add(anime);
            var episodeIds = new List<Guid>();
            for (var number = 1; number <= episodes; number++)
            {
                var episode = new Episode
                {
                    AnimeId = anime.Id,
                    SeasonNumber = 1,
                    Number = number,
                    Title = $"Episode {number}"
                };
                episodeIds.Add(episode.Id);
                Db.Add(episode);
            }

            if (externalId is not null)
            {
                Db.Add(new AnimeMetadata
                {
                    AnimeId = anime.Id,
                    Provider = AniListMetadataProvider.ProviderKey,
                    ExternalId = externalId,
                    PreferredTitle = title,
                    EpisodeCount = episodes
                });
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return new TestAnime(anime.Id, episodeIds);
        }

        public async Task MarkWatchedAsync(string profileId, TestAnime anime, params int[] numbers)
        {
            foreach (var number in numbers)
            {
                await CanonicalProgressSeed.SetAsync(Db, profileId, anime.Episodes[number - 1], 0, null, true);
            }

            Db.ChangeTracker.Clear();
        }

        private AnimeMetadataService Metadata() =>
            new(Db, [], accountStore, ReviewStore);

        private static void Attach<TPage>(TPage page)
            where TPage : PageModel
        {
            var services = new ServiceCollection()
                .AddSingleton<IModelMetadataProvider, EmptyModelMetadataProvider>()
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            page.PageContext = new PageContext
            {
                HttpContext = httpContext,
                // Typed like the runtime page ViewData, so the partial result
                // must not try to reuse it for a different model type.
                ViewData = new ViewDataDictionary<TPage>(
                    new EmptyModelMetadataProvider(),
                    new ModelStateDictionary())
            };
            page.TempData = new TempDataDictionary(httpContext, new NoTempDataProvider());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record TestAnime(Guid Id, IReadOnlyList<Guid> Episodes);

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoopFranchiseRelationSource : IFranchiseRelationSource
    {
        public Task<AniListRelatedMedia> GetRelatedAsync(WatchlistIdentity work, CancellationToken cancellationToken) =>
            Task.FromResult(new AniListRelatedMedia(null, []));
    }

    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
