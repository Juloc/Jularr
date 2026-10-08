using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Tests;

/// <summary>The owner's request queue (Admin → Requests): tabs, filters, search, paging, reopen.</summary>
[TestClass]
public sealed class AdminRequestQueryTests
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["alice"] = "Alice",
        ["bob"] = "Bob"
    };

    private static AcquisitionRequest Row(
        string title,
        AcquisitionRequestStatus status,
        string requester = "alice",
        MediaAcquisitionKind kind = MediaAcquisitionKind.Anime,
        string? audio = null,
        string? subtitle = null,
        AcquisitionRequestOptions? options = null,
        string? payloadJson = null) =>
        new(
            Guid.NewGuid(),
            kind,
            "test",
            title,
            title,
            subtitle,
            null,
            payloadJson ?? (audio is null ? options ?? AcquisitionRequestOptions.Default : (options ?? AcquisitionRequestOptions.Default) with { AudioLanguage = audio }).ToPayloadJson(),
            requester,
            status,
            null,
            null,
            null,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            null);

    [TestMethod]
    [DataRow(AcquisitionRequestStatus.Pending, AdminRequestTab.Open)]
    [DataRow(AcquisitionRequestStatus.Failed, AdminRequestTab.Approved)]
    [DataRow(AcquisitionRequestStatus.Approved, AdminRequestTab.Approved)]
    [DataRow(AcquisitionRequestStatus.Searching, AdminRequestTab.InProgress)]
    [DataRow(AcquisitionRequestStatus.Downloading, AdminRequestTab.InProgress)]
    [DataRow(AcquisitionRequestStatus.Importing, AdminRequestTab.InProgress)]
    [DataRow(AcquisitionRequestStatus.Completed, AdminRequestTab.Done)]
    [DataRow(AcquisitionRequestStatus.Rejected, AdminRequestTab.Rejected)]
    public void EveryStatusBelongsToExactlyOneTab(AcquisitionRequestStatus status, AdminRequestTab tab) =>
        Assert.AreEqual(tab, AdminRequestQuery.TabOf(status));

    [TestMethod]
    public void TabCountsAddUpAndTheTabListsOnlyItsOwnRequests()
    {
        var rows = new[]
        {
            Row("A", AcquisitionRequestStatus.Pending),
            Row("B", AcquisitionRequestStatus.Failed),
            Row("C", AcquisitionRequestStatus.Approved),
            Row("D", AcquisitionRequestStatus.Downloading),
            Row("E", AcquisitionRequestStatus.Completed),
            Row("F", AcquisitionRequestStatus.Rejected)
        };

        var open = AdminRequestQuery.Build(rows, new AdminRequestFilter(AdminRequestTab.Open, Sort: "title"), Names);

        Assert.AreEqual(5, open.TabCounts[AdminRequestTab.All]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Open]);
        Assert.AreEqual(2, open.TabCounts[AdminRequestTab.Approved]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.InProgress]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Done]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Rejected]);
        CollectionAssert.AreEqual(new[] { "A" }, open.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(1, open.Total);
    }

    [TestMethod]
    public void FiltersNarrowTheListAndTheCountsButTheTabAndStatusOnlyTheList()
    {
        var rows = new[]
        {
            Row("Frieren", AcquisitionRequestStatus.Pending, "alice", MediaAcquisitionKind.Anime, "ja"),
            Row("Dune", AcquisitionRequestStatus.Pending, "bob", MediaAcquisitionKind.Movie),
            Row("Dungeon Meshi", AcquisitionRequestStatus.Completed, "alice", MediaAcquisitionKind.Anime, "de"),
            Row("Berserk", AcquisitionRequestStatus.Completed, "bob", MediaAcquisitionKind.Manga)
        };

        var byKind = AdminRequestQuery.Build(rows, new AdminRequestFilter(Kind: MediaAcquisitionKind.Anime), Names);
        Assert.AreEqual(1, byKind.Total);
        Assert.AreEqual(1, byKind.TabCounts[AdminRequestTab.All]);

        var byLanguage = AdminRequestQuery.Build(rows, new AdminRequestFilter(AdminRequestTab.Done, Language: "de"), Names);
        CollectionAssert.AreEqual(new[] { "Dungeon Meshi" }, byLanguage.Items.Select(item => item.Title).ToArray());

        var byRequester = AdminRequestQuery.Build(rows, new AdminRequestFilter(RequesterProfileId: "bob"), Names);
        CollectionAssert.AreEquivalent(new[] { "Dune" }, byRequester.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(1, byRequester.TabCounts[AdminRequestTab.Open]);
        Assert.AreEqual(1, byRequester.TabCounts[AdminRequestTab.Done]);

        // Status narrows the list only: the tab numbers keep saying what each tab holds.
        var byStatus = AdminRequestQuery.Build(rows, new AdminRequestFilter(Status: AcquisitionRequestStatus.Completed), Names);
        Assert.AreEqual(2, byStatus.Total);
        Assert.AreEqual(2, byStatus.TabCounts[AdminRequestTab.All]);

        var emptyIntersection = AdminRequestQuery.Build(
            rows,
            new AdminRequestFilter(AdminRequestTab.Open, Status: AcquisitionRequestStatus.Completed),
            Names);
        Assert.AreEqual(0, emptyIntersection.Total);
    }

    [TestMethod]
    public void SearchMatchesTitleUnitAndRequesterName()
    {
        var rows = new[]
        {
            Row("Frieren", AcquisitionRequestStatus.Pending, "alice", subtitle: "Season 2"),
            Row("Dune", AcquisitionRequestStatus.Pending, "bob")
        };

        Assert.AreEqual(1, AdminRequestQuery.Build(rows, new AdminRequestFilter(Search: "frier"), Names).Total);
        Assert.AreEqual(1, AdminRequestQuery.Build(rows, new AdminRequestFilter(Search: "season 2"), Names).Total);
        var byRequester = AdminRequestQuery.Build(rows, new AdminRequestFilter(Search: "BOB"), Names);
        CollectionAssert.AreEqual(new[] { "Dune" }, byRequester.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(0, AdminRequestQuery.Build(rows, new AdminRequestFilter(Search: "nothing"), Names).Total);
    }

    [TestMethod]
    public void SeasonFilterUsesAnimeSelectionAndResolvedTvSeasons()
    {
        var anime = Row("Anime season two", AcquisitionRequestStatus.Pending, options: new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [2] });
        var tvSeasonId = Guid.NewGuid();
        var tvPayload = new VideoRequestPayload(Guid.NewGuid(), "TV season two", null, VideoRequestScope.Custom, [], false, SelectedSeasonIds: [tvSeasonId]).Serialize();
        var tv = Row("TV season two", AcquisitionRequestStatus.Approved, kind: MediaAcquisitionKind.Tv, payloadJson: tvPayload);
        var other = Row("Other season", AcquisitionRequestStatus.Rejected, options: new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [1] });
        var rows = new[] { anime, tv, other };
        IReadOnlyDictionary<Guid, IReadOnlyList<int>> videoSeasons = new Dictionary<Guid, IReadOnlyList<int>> { [tv.Id] = [2] };

        var page = AdminRequestQuery.Build(rows, new AdminRequestFilter(Season: 2), Names, videoSeasons);

        CollectionAssert.AreEquivalent(new[] { anime.Id, tv.Id }, page.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(2, page.TabCounts[AdminRequestTab.All]);
        CollectionAssert.AreEqual(new[] { 1, 2 }, page.Seasons.ToArray());
        Assert.IsTrue(page.Filter.HasNarrowing);
    }

    [TestMethod]
    public void LanguageChoicesListTheRequestedLanguagesOnceInFormOrder()
    {
        var rows = new[]
        {
            Row("A", AcquisitionRequestStatus.Pending, audio: "de"),
            Row("B", AcquisitionRequestStatus.Pending, audio: "ja"),
            Row("C", AcquisitionRequestStatus.Rejected, audio: "de"),
            Row("D", AcquisitionRequestStatus.Pending, kind: MediaAcquisitionKind.Book)
        };

        var page = AdminRequestQuery.Build(rows, new AdminRequestFilter(AdminRequestTab.Open), Names);

        CollectionAssert.AreEqual(new[] { "ja", "de" }, page.Languages.ToArray());
    }

    [TestMethod]
    public void PagesSupportTheRequestedSizeAndAPagePastTheEndShowsTheLast()
    {
        var rows = Enumerable.Range(1, 45).Select(index => Row($"T{index:00}", AcquisitionRequestStatus.Pending)).ToArray();

        var second = AdminRequestQuery.Build(rows, new AdminRequestFilter(Page: 2, PageSize: 20, Sort: "title"), Names);
        Assert.AreEqual(3, second.PageCount);
        Assert.AreEqual(20, second.Items.Count);
        Assert.AreEqual("T21", second.Items[0].Title);
        Assert.IsTrue(second.HasPrevious && second.HasNext);

        var beyond = AdminRequestQuery.Build(rows, new AdminRequestFilter(Page: 99, PageSize: 20, Sort: "title"), Names);
        Assert.AreEqual(3, beyond.Page);
        Assert.AreEqual(5, beyond.Items.Count);
        Assert.IsFalse(beyond.HasNext);

        var custom = AdminRequestQuery.Build(rows, new AdminRequestFilter(PageSize: 37, Sort: "title"), Names);
        Assert.AreEqual(37, custom.PageSize);
        Assert.AreEqual(2, custom.PageCount);

        var none = AdminRequestQuery.Build([], new AdminRequestFilter(Page: 4), Names);
        Assert.AreEqual(1, none.Page);
        Assert.AreEqual(1, none.PageCount);
    }

    [TestMethod]
    public void UnknownAddressValuesMeanNoFilter()
    {
        Assert.AreEqual(AdminRequestTab.All, AdminRequestQuery.ParseTab("nonsense"));
        Assert.AreEqual(AdminRequestTab.InProgress, AdminRequestQuery.ParseTab("progress"));
        Assert.IsNull(AdminRequestQuery.TryParseKind("torrent"));
        Assert.AreEqual(MediaAcquisitionKind.LightNovel, AdminRequestQuery.TryParseKind("lightNovel"));
        Assert.IsNull(AdminRequestQuery.TryParseStatus("nope"));
        Assert.AreEqual(AcquisitionRequestStatus.Failed, AdminRequestQuery.TryParseStatus("failed"));
        Assert.AreEqual("newest", AdminRequestQuery.NormalizeSort("unknown"));
        Assert.AreEqual("oldest", AdminRequestQuery.NormalizeSort(" OLDEST "));
        Assert.AreEqual(500, AdminRequestQuery.NormalizePageSize(500));
        Assert.AreEqual(AdminRequestQuery.DefaultPageSize, AdminRequestQuery.NormalizePageSize(501));
    }

    [TestMethod]
    public void SortOrdersTheFilteredRowsAndRemainsInTheNormalizedFilter()
    {
        var rows = new[]
        {
            Row("Zebra", AcquisitionRequestStatus.Pending),
            Row("Alpha", AcquisitionRequestStatus.Pending),
            Row("Middle", AcquisitionRequestStatus.Pending)
        };

        var page = AdminRequestQuery.Build(rows, new AdminRequestFilter(Sort: "title"), Names);

        CollectionAssert.AreEqual(new[] { "Alpha", "Middle", "Zebra" }, page.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual("title", page.Filter.Sort);
        Assert.IsTrue(page.Filter.HasNarrowing);
    }

    [TestMethod]
    public async Task StoreListsEveryRequestWaitingOnesFirst()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = fixture.Store;
        Task<AcquisitionRequest> Create(string id, AcquisitionRequestStatus status) => store.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", id, id, null, null),
            "alice",
            status,
            status == AcquisitionRequestStatus.Pending ? null : "owner",
            CancellationToken.None);

        await Create("done", AcquisitionRequestStatus.Completed);
        await Create("rejected", AcquisitionRequestStatus.Rejected);
        await Create("waiting", AcquisitionRequestStatus.Pending);

        var all = await store.ListAllAsync(100, CancellationToken.None);

        Assert.AreEqual(3, all.Count);
        Assert.AreEqual("waiting", all[0].Title);
    }

    [TestMethod]
    public async Task ReopenPutsARejectedRequestBackUnlessTheTitleIsOpenAgain()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var owner = fixture.Service("owner", isOwner: true);
        var user = fixture.Service("alice", isOwner: false);
        var other = fixture.Service("bob", isOwner: false);
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "dune", "Dune", null, null);
        var store = fixture.Store;

        var request = await user.SubmitAsync(draft, CancellationToken.None);
        await owner.RejectAsync(request.Id, null, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, (await store.GetAsync(request.Id, CancellationToken.None))!.Status);

        var reopened = await owner.ReopenAsync(request.Id, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Pending, reopened.Status);

        // Reopening something that is not rejected changes nothing.
        var again = await owner.ReopenAsync(request.Id, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Pending, again.Status);

        // A second request for the same title is open while the first stays rejected.
        await owner.RejectAsync(request.Id, null, CancellationToken.None);
        var second = await other.SubmitAsync(draft, CancellationToken.None);
        Assert.AreNotEqual(request.Id, second.Id);
        var blocked = await owner.ReopenAsync(request.Id, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, blocked.Status);

        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => user.ReopenAsync(request.Id, CancellationToken.None));
    }
}
