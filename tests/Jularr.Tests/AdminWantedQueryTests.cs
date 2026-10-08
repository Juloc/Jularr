using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Localization;
using Jularr.Web.Pages.Admin;

namespace Jularr.Tests;

/// <summary>Admin → Wanted: which state each source is in, and how the list is narrowed, sorted and paged.</summary>
[TestClass]
public sealed class AdminWantedQueryTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static readonly QualityProfileState Profiles = new(
        QualityProfileState.CurrentVersion,
        [
            new QualityProfile("default-anime", "Anime 1080p", [], [], false, null, null, null, [], [], [], [], []),
            new QualityProfile("remux", "Remux", [], [], false, null, null, null, [], [], [], [], [])
        ],
        new Dictionary<string, string> { ["anime"] = "default-anime" },
        new Dictionary<string, string>());

    [TestMethod]
    [DataRow(AcquisitionRequestStatus.Approved, 0, WantedStatus.Requested)]
    [DataRow(AcquisitionRequestStatus.Approved, 3, WantedStatus.Missing)]
    [DataRow(AcquisitionRequestStatus.Searching, 0, WantedStatus.Searching)]
    [DataRow(AcquisitionRequestStatus.Downloading, 1, WantedStatus.Downloading)]
    [DataRow(AcquisitionRequestStatus.Importing, 1, WantedStatus.Importing)]
    [DataRow(AcquisitionRequestStatus.Failed, 12, WantedStatus.Failed)]
    public void AnApprovedRequestIsInTheWorklistInTheStateOfItsAcquisition(
        AcquisitionRequestStatus status,
        int searches,
        WantedStatus expected) =>
        Assert.AreEqual(expected, AdminWantedQuery.StatusOfRequest(status, searches));

    [TestMethod]
    [DataRow(AcquisitionRequestStatus.Pending)]
    [DataRow(AcquisitionRequestStatus.Completed)]
    [DataRow(AcquisitionRequestStatus.Rejected)]
    public void RequestsThatWaitForTheOwnerOrAreFinishedAreNotWanted(AcquisitionRequestStatus status) =>
        Assert.IsNull(AdminWantedQuery.StatusOfRequest(status, 0));

    [TestMethod]
    [DataRow(null, WantedStatus.Missing)]
    [DataRow(AcquisitionAttemptStatus.None, WantedStatus.Missing)]
    [DataRow(AcquisitionAttemptStatus.Pending, WantedStatus.Searching)]
    [DataRow(AcquisitionAttemptStatus.Grabbed, WantedStatus.Downloading)]
    [DataRow(AcquisitionAttemptStatus.Failed, WantedStatus.Failed)]
    public void AMonitoredUnitIsInTheStateOfItsLastAttempt(AcquisitionAttemptStatus? attempt, WantedStatus expected) =>
        Assert.AreEqual(expected, AdminWantedQuery.StatusOfAttempt(attempt));

    [TestMethod]
    public void EveryStateBelongsToOneTabAndHasAName()
    {
        foreach (var status in Enum.GetValues<WantedStatus>())
        {
            Assert.AreNotEqual(AdminWantedTab.All, AdminWantedQuery.TabOf(status), status.ToString());
            Assert.IsTrue(
                UiTranslationResources.TryGet("admin.wanted.status." + AdminWantedQuery.StatusName(status), out _),
                $"{status} needs a catalog text.");
        }

        Assert.AreEqual(AdminWantedTab.Searching, AdminWantedQuery.TabOf(WantedStatus.Downloading));
        Assert.AreEqual(AdminWantedTab.Searching, AdminWantedQuery.TabOf(WantedStatus.Importing));
    }

    [TestMethod]
    public void EveryMediaTypeAndAgeHasACatalogText()
    {
        foreach (var kind in Enum.GetValues<MediaAcquisitionKind>())
        {
            Assert.IsTrue(UiTranslationResources.TryGet(MediaKindLabelKeys.Name(kind), out _), kind.ToString());
            Assert.IsTrue(UiTranslationResources.TryGet(MediaKindLabelKeys.Operation(kind), out _), kind.ToString());
        }

        foreach (var key in new[] { "now", "minutes", "hours", "days" })
        {
            Assert.IsTrue(UiTranslationResources.TryGet("admin.wanted.age." + key, out _), key);
        }
    }

    [TestMethod]
    public void TheAddressRoundTripsTabsSortsAndMediaTypes()
    {
        foreach (var tab in Enum.GetValues<AdminWantedTab>())
        {
            Assert.AreEqual(tab, AdminWantedQuery.ParseTab(AdminWantedQuery.TabName(tab)));
        }

        foreach (var sort in Enum.GetValues<AdminWantedSort>())
        {
            Assert.AreEqual(sort, AdminWantedQuery.ParseSort(AdminWantedQuery.SortName(sort)));
        }

        Assert.AreEqual(AdminWantedTab.All, AdminWantedQuery.ParseTab("nonsense"));
        Assert.AreEqual(AdminWantedSort.LastSearch, AdminWantedQuery.ParseSort(null));
        Assert.AreEqual(MediaAcquisitionKind.LightNovel, AdminWantedQuery.TryParseKind(" lightNovel "));
        Assert.IsNull(AdminWantedQuery.TryParseKind("nonsense"));

        Assert.AreEqual("/Admin/Wanted", WantedModel.Href(new AdminWantedFilter()));
        Assert.AreEqual(
            "/Admin/Wanted?tab=failed&type=book&lang=de&profile=remux&q=dune%20two&sort=title&p=2",
            WantedModel.Href(new AdminWantedFilter(
                AdminWantedTab.Failed, MediaAcquisitionKind.Book, "de", "remux", " dune two ", AdminWantedSort.Title, 2)));
    }

    [TestMethod]
    [DataRow(0, WantedAgeUnit.JustNow, 0)]
    [DataRow(59, WantedAgeUnit.JustNow, 0)]
    [DataRow(60, WantedAgeUnit.Minutes, 1)]
    [DataRow(59 * 60, WantedAgeUnit.Minutes, 59)]
    [DataRow(3600, WantedAgeUnit.Hours, 1)]
    [DataRow(23 * 3600 + 3599, WantedAgeUnit.Hours, 23)]
    [DataRow(86400, WantedAgeUnit.Days, 1)]
    [DataRow(5 * 86400 + 100, WantedAgeUnit.Days, 5)]
    [DataRow(-30, WantedAgeUnit.JustNow, 0)]
    public void AgesRoundDownToTheirUnit(int secondsAgo, WantedAgeUnit unit, int count) =>
        Assert.AreEqual((unit, count), AdminWantedQuery.AgeOf(Now.AddSeconds(-secondsAgo), Now));

    [TestMethod]
    public void TabCountsFollowEveryFilterButTheTabAndFiltersNarrowTheList()
    {
        var items = new[]
        {
            Item("a", MediaAcquisitionKind.Book, WantedStatus.Requested, "Project Hail Mary", languages: ["en"]),
            Item("b", MediaAcquisitionKind.Manga, WantedStatus.Missing, "Berserk", volume: 42, languages: ["de"]),
            Item("c", MediaAcquisitionKind.Manga, WantedStatus.Missing, "Berserk", volume: 41, languages: ["de"]),
            Item("d", MediaAcquisitionKind.Anime, WantedStatus.Downloading, "Frieren", season: 1, episode: 28, profileId: "remux", profileName: "Remux"),
            Item("e", MediaAcquisitionKind.Anime, WantedStatus.Failed, "Kaiju No. 8", season: 1, episode: 12, profileId: "default-anime", profileName: "Anime 1080p")
        };

        var all = AdminWantedQuery.Build(items, new AdminWantedFilter());
        Assert.AreEqual(5, all.TabCounts[AdminWantedTab.All]);
        Assert.AreEqual(1, all.TabCounts[AdminWantedTab.Requested]);
        Assert.AreEqual(2, all.TabCounts[AdminWantedTab.Missing]);
        Assert.AreEqual(1, all.TabCounts[AdminWantedTab.Searching]);
        Assert.AreEqual(1, all.TabCounts[AdminWantedTab.Failed]);
        CollectionAssert.AreEqual(
            new[] { MediaAcquisitionKind.Anime, MediaAcquisitionKind.Manga, MediaAcquisitionKind.Book },
            all.Kinds.ToArray());
        CollectionAssert.AreEqual(new[] { "en", "de" }, all.Languages.ToArray());
        CollectionAssert.AreEqual(
            new[] { "Anime 1080p", "Remux" },
            all.Profiles.Select(profile => profile.Name).ToArray());

        var manga = AdminWantedQuery.Build(items, new AdminWantedFilter(Tab: AdminWantedTab.Failed, Kind: MediaAcquisitionKind.Manga));
        Assert.AreEqual(0, manga.Total);
        Assert.AreEqual(2, manga.TabCounts[AdminWantedTab.All], "The tab does not narrow the counts, the media type does.");
        Assert.AreEqual(2, manga.TabCounts[AdminWantedTab.Missing]);
        Assert.AreEqual(0, manga.TabCounts[AdminWantedTab.Failed]);

        Assert.AreEqual(2, AdminWantedQuery.Build(items, new AdminWantedFilter(Language: "de")).Total);
        Assert.AreEqual("d", AdminWantedQuery.Build(items, new AdminWantedFilter(ProfileId: "REMUX")).Items.Single().Id);
        Assert.AreEqual("a", AdminWantedQuery.Build(items, new AdminWantedFilter(Search: " hail ")).Items.Single().Id);
        Assert.AreEqual("e", AdminWantedQuery.Build(items, new AdminWantedFilter(Tab: AdminWantedTab.Failed)).Items.Single().Id);
    }

    [TestMethod]
    public void SortingOrdersByLastSearchTitleOrHowLongAnItemHasBeenWanted()
    {
        var items = new[]
        {
            Item("old", MediaAcquisitionKind.Book, WantedStatus.Missing, "Zeta", since: Now.AddDays(-9), lastSearch: Now.AddDays(-3)),
            Item("new", MediaAcquisitionKind.Book, WantedStatus.Missing, "Alpha", since: Now.AddDays(-1), lastSearch: Now.AddHours(-2)),
            Item("never", MediaAcquisitionKind.Book, WantedStatus.Requested, "Mu", since: Now.AddDays(-4)),
            Item("ep2", MediaAcquisitionKind.Anime, WantedStatus.Missing, "Beta", season: 1, episode: 2, since: Now.AddDays(-2)),
            Item("ep10", MediaAcquisitionKind.Anime, WantedStatus.Missing, "Beta", season: 1, episode: 10, since: Now.AddDays(-2))
        };

        string[] Order(AdminWantedSort sort) =>
            AdminWantedQuery.Build(items, new AdminWantedFilter(Sort: sort)).Items.Select(item => item.Id).ToArray();

        CollectionAssert.AreEqual(new[] { "new", "old", "ep2", "ep10", "never" }, Order(AdminWantedSort.LastSearch));
        CollectionAssert.AreEqual(new[] { "new", "ep2", "ep10", "never", "old" }, Order(AdminWantedSort.Title));
        CollectionAssert.AreEqual(new[] { "old", "never", "ep2", "ep10", "new" }, Order(AdminWantedSort.Waiting));
    }

    [TestMethod]
    public void PagingClampsAPagePastTheEndToTheLastPage()
    {
        var items = Enumerable.Range(0, AdminWantedQuery.PageSize + 1)
            .Select(index => Item($"i{index:00}", MediaAcquisitionKind.Book, WantedStatus.Missing, $"Book {index:00}"))
            .ToArray();

        var first = AdminWantedQuery.Build(items, new AdminWantedFilter(Sort: AdminWantedSort.Title));
        Assert.AreEqual(2, first.PageCount);
        Assert.AreEqual(AdminWantedQuery.PageSize, first.Items.Count);
        Assert.IsTrue(first.HasNext);
        Assert.IsFalse(first.HasPrevious);

        var last = AdminWantedQuery.Build(items, new AdminWantedFilter(Sort: AdminWantedSort.Title, Page: 99));
        Assert.AreEqual(2, last.Page);
        Assert.AreEqual(AdminWantedQuery.PageSize, last.Offset);
        Assert.AreEqual("Book 20", last.Items.Single().Title);
        Assert.IsTrue(last.HasPrevious);
        Assert.IsFalse(last.HasNext);

        var empty = AdminWantedQuery.Build([], new AdminWantedFilter(Page: 5));
        Assert.AreEqual(1, empty.Page);
        Assert.AreEqual(0, empty.Total);
    }

    [TestMethod]
    public void AMangaRequestThatSearchedWithoutAReleaseIsMissingWithItsVolumeLanguageAndNextSearch()
    {
        var request = Request(
            MediaAcquisitionKind.Manga,
            AcquisitionRequestStatus.Approved,
            """{"title":"Berserk","aliases":[],"requestedVolume":42,"preferredLanguages":["DE"," de ","en"],"searches":2,"nextSearchUtc":"2026-10-01T06:00:00Z"}""",
            message: "No release yet. Searching again 2026-10-01 06:00 UTC.");

        var item = WantedListService.FromRequest(request, hasExecutor: true, Profiles)!;

        Assert.AreEqual(WantedStatus.Missing, item.Status);
        Assert.AreEqual(WantedSource.Request, item.Source);
        Assert.AreEqual(42, item.Volume);
        CollectionAssert.AreEqual(new[] { "de", "en" }, item.Languages.ToArray());
        Assert.AreEqual(new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc), item.NextSearchUtc);
        Assert.IsNotNull(item.LastSearchUtc, "A searched request shows when it last changed.");
        Assert.IsTrue(item.CanSearch);
        Assert.IsNull(item.ProfileId, "Only anime has a quality profile.");
        Assert.AreEqual("No release yet. Searching again 2026-10-01 06:00 UTC.", item.Note);
    }

    [TestMethod]
    public void AnUnsearchedRequestHasNoLastSearchAndAKindWithoutAnExecutorCannotSearch()
    {
        var movie = WantedListService.FromRequest(
            Request(MediaAcquisitionKind.Movie, AcquisitionRequestStatus.Approved, """{"unrelated":true,"searches":"many"}"""),
            hasExecutor: false,
            Profiles)!;

        Assert.AreEqual(WantedStatus.Requested, movie.Status);
        Assert.IsNull(movie.LastSearchUtc);
        Assert.IsFalse(movie.CanSearch);

        var downloading = WantedListService.FromRequest(
            Request(MediaAcquisitionKind.Book, AcquisitionRequestStatus.Downloading, null),
            hasExecutor: true,
            Profiles)!;
        Assert.IsFalse(downloading.CanSearch, "A download in progress is not searched again.");
        Assert.IsNotNull(downloading.LastSearchUtc);

        var failed = WantedListService.FromRequest(
            Request(MediaAcquisitionKind.Book, AcquisitionRequestStatus.Failed, null),
            hasExecutor: true,
            Profiles)!;
        Assert.IsTrue(failed.CanSearch);

        Assert.IsNull(WantedListService.FromRequest(
            Request(MediaAcquisitionKind.Book, AcquisitionRequestStatus.Pending, null),
            hasExecutor: true,
            Profiles));
    }

    [TestMethod]
    public void AnAnimeRequestCarriesItsScopeLanguageAndQualityProfile()
    {
        var options = new AcquisitionRequestOptions
        {
            Scope = RequestScope.Episodes,
            Episodes = [new RequestEpisode(1, 1), new RequestEpisode(1, 2), new RequestEpisode(2, 5)],
            AudioLanguage = "ja"
        }.Validate();
        var item = WantedListService.FromRequest(
            Request(MediaAcquisitionKind.Anime, AcquisitionRequestStatus.Approved, options.ToPayloadJson(), resultUrl: "/Library/Anime/abc"),
            hasExecutor: true,
            Profiles)!;

        Assert.AreEqual(RequestScope.Episodes, item.Scope);
        Assert.AreEqual("S01E01-02, S02E05", item.Selection);
        CollectionAssert.AreEqual(new[] { "ja" }, item.Languages.ToArray());
        Assert.AreEqual("default-anime", item.ProfileId);
        Assert.AreEqual("Anime 1080p", item.ProfileName);
        Assert.AreEqual("/Library/Anime/abc", item.DetailUrl);

        var external = WantedListService.FromRequest(
            Request(MediaAcquisitionKind.Anime, AcquisitionRequestStatus.Failed, null, resultUrl: "https://elsewhere.example/x"),
            hasExecutor: true,
            Profiles)!;
        Assert.IsNull(external.DetailUrl, "Only addresses on this server are linked.");
    }

    [TestMethod]
    public void AMonitoredEpisodeShowsItsAttemptAndOnlyRetriesWhileFailed()
    {
        var key = new AnimeEpisodeKey("frieren", 2, 13);
        var wanted = new WantedUnit(key, WantedReason.CutoffUnmet, new DateTimeOffset(Now.AddDays(-2)));
        var failed = new AcquisitionAttempt(
            key,
            AcquisitionAttemptStatus.Failed,
            "release",
            3,
            new DateTimeOffset(Now.AddHours(-5)),
            new DateTimeOffset(Now.AddHours(1)));
        var animeId = Guid.NewGuid();

        var item = WantedListService.FromUnit(wanted, failed, "Frieren", animeId, "/cover.webp", "remux", "Remux");

        Assert.AreEqual(WantedStatus.Failed, item.Status);
        Assert.AreEqual(WantedSource.Monitored, item.Source);
        Assert.AreEqual(2, item.Season);
        Assert.AreEqual(13, item.Episode);
        Assert.IsTrue(item.IsUpgrade);
        Assert.AreEqual(3, item.Failures);
        Assert.AreEqual(Now.AddHours(-5), item.LastSearchUtc);
        Assert.AreEqual(Now.AddHours(1), item.NextSearchUtc);
        Assert.AreEqual(Now.AddDays(-2), item.SinceUtc);
        Assert.AreEqual($"/Library/Anime/{animeId:D}", item.DetailUrl);
        Assert.IsTrue(item.CanSearch);

        var grabbed = WantedListService.FromUnit(
            wanted with { Reason = WantedReason.Missing },
            failed with { Status = AcquisitionAttemptStatus.Grabbed },
            "Frieren",
            animeId,
            null,
            null,
            null);
        Assert.AreEqual(WantedStatus.Downloading, grabbed.Status);
        Assert.IsNull(grabbed.NextSearchUtc, "Only a failed attempt has a retry.");

        var orphan = WantedListService.FromUnit(wanted, null, null, null, null, null, null);
        Assert.AreEqual("frieren", orphan.Title, "An unknown anime shows its key.");
        Assert.AreEqual(WantedStatus.Missing, orphan.Status);
        Assert.IsFalse(orphan.CanSearch, "Without a library entry there is nothing to search for.");
        Assert.IsNull(orphan.DetailUrl);
    }

    private static WantedRow Item(
        string id,
        MediaAcquisitionKind kind,
        WantedStatus status,
        string title,
        int? season = null,
        int? episode = null,
        int? volume = null,
        string[]? languages = null,
        string? profileId = null,
        string? profileName = null,
        DateTime? since = null,
        DateTime? lastSearch = null) =>
        new(id, kind == MediaAcquisitionKind.Anime ? WantedSource.Monitored : WantedSource.Request, kind, title, status, since ?? Now.AddDays(-1))
        {
            Season = season,
            Episode = episode,
            Volume = volume,
            Languages = languages ?? [],
            ProfileId = profileId,
            ProfileName = profileName,
            LastSearchUtc = lastSearch
        };

    private static AcquisitionRequest Request(
        MediaAcquisitionKind kind,
        AcquisitionRequestStatus status,
        string? payload,
        string? message = null,
        string? resultUrl = null) =>
        new(
            Guid.NewGuid(),
            kind,
            "provider",
            "external",
            "Title",
            null,
            null,
            payload,
            "profile",
            status,
            message,
            null,
            resultUrl,
            Now.AddDays(-3),
            Now.AddHours(-4),
            null,
            null);
}
