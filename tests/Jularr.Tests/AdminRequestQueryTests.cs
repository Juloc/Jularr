using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

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
    [DataRow(AcquisitionRequestStatus.Failed, AdminRequestTab.Failed)]
    [DataRow(AcquisitionRequestStatus.Approved, AdminRequestTab.Approved)]
    [DataRow(AcquisitionRequestStatus.Searching, AdminRequestTab.InProgress)]
    [DataRow(AcquisitionRequestStatus.Downloading, AdminRequestTab.InProgress)]
    [DataRow(AcquisitionRequestStatus.Importing, AdminRequestTab.InProgress)]
    [DataRow(AcquisitionRequestStatus.Completed, AdminRequestTab.Done)]
    [DataRow(AcquisitionRequestStatus.Rejected, AdminRequestTab.Rejected)]
    public void EveryStatusBelongsToExactlyOneTab(AcquisitionRequestStatus status, AdminRequestTab tab) =>
        Assert.AreEqual(tab, AdminRequestQuery.TabOf(status));

    [TestMethod]
    public async Task TabCountsAddUpAndTheTabListsOnlyItsOwnRequests()
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

        var open = await QueryAsync(rows, new AdminRequestFilter(AdminRequestTab.Open, Sort: "title"), Names);

        Assert.AreEqual(6, open.TabCounts[AdminRequestTab.All]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Open]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Approved]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Failed]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.InProgress]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Done]);
        Assert.AreEqual(1, open.TabCounts[AdminRequestTab.Rejected]);
        CollectionAssert.AreEqual(new[] { "A" }, open.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(1, open.Total);
    }

    [TestMethod]
    public async Task AllIncludesEveryStatusAcrossPagesAndHonorsAnExplicitStatusFilter()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var statuses = Enum.GetValues<AcquisitionRequestStatus>();
        foreach (var status in statuses)
        {
            await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", status.ToString(), status.ToString(), null, null), "alice", status, null, CancellationToken.None);
        }

        var ids = new HashSet<Guid>();
        var includedStatuses = new HashSet<AcquisitionRequestStatus>();
        for (var pageNumber = 1; pageNumber <= (statuses.Length + 1) / 2; pageNumber++)
        {
            var page = (await fixture.Store.ReadQueueAsync(new AdminRequestFilter(Page: pageNumber, PageSize: 2), [MediaAcquisitionKind.Book], Names, CancellationToken.None)).Page;
            Assert.AreEqual(statuses.Length, page.Total);
            Assert.AreEqual(statuses.Length, page.TabCounts[AdminRequestTab.All]);
            Assert.AreEqual((statuses.Length + 1) / 2, page.PageCount);
            foreach (var item in page.Items)
            {
                Assert.IsTrue(ids.Add(item.Id));
                includedStatuses.Add(item.Status);
            }
        }

        CollectionAssert.AreEquivalent(statuses, includedStatuses.ToArray());
        var completed = (await fixture.Store.ReadQueueAsync(new AdminRequestFilter(Status: AcquisitionRequestStatus.Completed), [MediaAcquisitionKind.Book], Names, CancellationToken.None)).Page;
        Assert.AreEqual(1, completed.Total);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Items.Single().Status);
        Assert.AreEqual(statuses.Length, completed.TabCounts[AdminRequestTab.All]);
    }

    [TestMethod]
    public async Task FiltersNarrowTheListAndTheCountsButTheTabAndStatusOnlyTheList()
    {
        var rows = new[]
        {
            Row("Frieren", AcquisitionRequestStatus.Pending, "alice", MediaAcquisitionKind.Anime, "ja"),
            Row("Dune", AcquisitionRequestStatus.Pending, "bob", MediaAcquisitionKind.Movie),
            Row("Dungeon Meshi", AcquisitionRequestStatus.Completed, "alice", MediaAcquisitionKind.Anime, "de"),
            Row("Berserk", AcquisitionRequestStatus.Completed, "bob", MediaAcquisitionKind.Manga)
        };

        var byKind = await QueryAsync(rows, new AdminRequestFilter(Kind: MediaAcquisitionKind.Anime), Names);
        Assert.AreEqual(2, byKind.Total);
        Assert.AreEqual(2, byKind.TabCounts[AdminRequestTab.All]);

        var byLanguage = await QueryAsync(rows, new AdminRequestFilter(AdminRequestTab.Done, Language: "de"), Names);
        CollectionAssert.AreEqual(new[] { "Dungeon Meshi" }, byLanguage.Items.Select(item => item.Title).ToArray());

        var byRequester = await QueryAsync(rows, new AdminRequestFilter(RequesterProfileId: "bob"), Names);
        CollectionAssert.AreEquivalent(new[] { "Dune", "Berserk" }, byRequester.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(1, byRequester.TabCounts[AdminRequestTab.Open]);
        Assert.AreEqual(1, byRequester.TabCounts[AdminRequestTab.Done]);

        // Status narrows the list only: the tab numbers keep saying what each tab holds.
        var byStatus = await QueryAsync(rows, new AdminRequestFilter(Status: AcquisitionRequestStatus.Completed), Names);
        Assert.AreEqual(2, byStatus.Total);
        Assert.AreEqual(4, byStatus.TabCounts[AdminRequestTab.All]);

        var emptyIntersection = await QueryAsync(
            rows,
            new AdminRequestFilter(AdminRequestTab.Open, Status: AcquisitionRequestStatus.Completed),
            Names);
        Assert.AreEqual(0, emptyIntersection.Total);
    }

    [TestMethod]
    public async Task SearchMatchesTitleUnitAndRequesterName()
    {
        var rows = new[]
        {
            Row("Frieren", AcquisitionRequestStatus.Pending, "alice", subtitle: "Season 2"),
            Row("Dune", AcquisitionRequestStatus.Pending, "bob")
        };

        Assert.AreEqual(1, (await QueryAsync(rows, new AdminRequestFilter(Search: "frier"), Names)).Total);
        Assert.AreEqual(1, (await QueryAsync(rows, new AdminRequestFilter(Search: "season 2"), Names)).Total);
        var byRequester = await QueryAsync(rows, new AdminRequestFilter(Search: "BOB"), Names);
        CollectionAssert.AreEqual(new[] { "Dune" }, byRequester.Items.Select(item => item.Title).ToArray());
        Assert.AreEqual(0, (await QueryAsync(rows, new AdminRequestFilter(Search: "nothing"), Names)).Total);
    }

    [TestMethod]
    public async Task SeasonFilterUsesAnimeSelectionAndResolvedTvSeasons()
    {
        var anime = Row("Anime season two", AcquisitionRequestStatus.Pending, options: new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [2] });
        var tvSeasonId = Guid.NewGuid();
        var tvPayload = (new VideoRequestPayload(42, "TV season two", null) { Requested = new VideoRequestScopeChoice(VideoRequestScope.Custom, [tvSeasonId], [], false) }).Serialize();
        var tv = Row("TV season two", AcquisitionRequestStatus.Approved, kind: MediaAcquisitionKind.Tv, payloadJson: tvPayload);
        var other = Row("Other season", AcquisitionRequestStatus.Rejected, options: new AcquisitionRequestOptions { Scope = RequestScope.Seasons, Seasons = [1] });
        var rows = new[] { anime, tv, other };
        IReadOnlyDictionary<Guid, IReadOnlyList<int>> videoSeasons = new Dictionary<Guid, IReadOnlyList<int>> { [tv.Id] = [2] };

        var page = await QueryAsync(rows, new AdminRequestFilter(Season: 2), Names, videoSeasons);

        CollectionAssert.AreEquivalent(new[] { anime.Id, tv.Id }, page.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(2, page.TabCounts[AdminRequestTab.All]);
        CollectionAssert.AreEqual(new[] { 1, 2 }, page.Seasons.ToArray());
        Assert.IsTrue(page.Filter.HasNarrowing);
    }

    [TestMethod]
    public async Task ApprovedTvKeepsItsRequestedSeasonAfterThePayloadChoiceWasConsumed()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var work = new Work { CanonicalTitle = "Series", MediaType = WorkMediaType.Series };
        var season = new WorkSeason { WorkId = work.Id, SeasonNumber = 2 };
        var episode = new WorkEpisode { WorkId = work.Id, SeasonId = season.Id, SeasonNumber = 2, EpisodeNumber = 1 };
        fixture.Db.Works.Add(work);
        await fixture.Db.SaveChangesAsync();
        season.WorkId = work.Id;
        episode.WorkId = work.Id;
        fixture.Db.AddRange(season, episode);
        await fixture.Db.SaveChangesAsync();
        var payload = new VideoRequestPayload(work.Id, work.CanonicalTitle, null) { Requested = new VideoRequestScopeChoice(VideoRequestScope.Custom, [season.Id], [], false) };
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Tv, "test", "series", work.CanonicalTitle, null, null, payload.Serialize());
        var request = await fixture.Store.CreateAsync(draft, "alice", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        await new RequestIntent(fixture.Db, TimeProvider.System).RecordAsync(request, CancellationToken.None);
        await fixture.Store.PatchPayloadAsync(request.Id, _ => (payload with { Requested = null }).Serialize(), CancellationToken.None);
        request = (await fixture.Store.GetAsync(request.Id, CancellationToken.None))!;
        var resolved = await new VideoRequestWorkResolver(fixture.Db).ResolveSeasonNumbersAsync([request], CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 2 }, resolved[request.Id].ToArray());
        var page = await fixture.Store.ReadQueueAsync(new AdminRequestFilter(Season: 2), [MediaAcquisitionKind.Tv], Names, CancellationToken.None);
        Assert.AreEqual(request.Id, page.Page.Items.Single().Id);
        CollectionAssert.AreEqual(new[] { 2 }, page.Page.Seasons.ToArray());
    }

    [TestMethod]
    public async Task LanguageChoicesListTheRequestedLanguagesOnceInFormOrder()
    {
        var rows = new[]
        {
            Row("A", AcquisitionRequestStatus.Pending, audio: "de"),
            Row("B", AcquisitionRequestStatus.Pending, audio: "ja"),
            Row("C", AcquisitionRequestStatus.Rejected, audio: "de"),
            Row("D", AcquisitionRequestStatus.Pending, kind: MediaAcquisitionKind.Book)
        };

        var page = await QueryAsync(rows, new AdminRequestFilter(AdminRequestTab.Open), Names);

        CollectionAssert.AreEqual(new[] { "ja", "de" }, page.Languages.ToArray());
    }

    [TestMethod]
    public async Task PagesSupportTheRequestedSizeAndAPagePastTheEndShowsTheLast()
    {
        var rows = Enumerable.Range(1, 45).Select(index => Row($"T{index:00}", AcquisitionRequestStatus.Pending)).ToArray();

        var second = await QueryAsync(rows, new AdminRequestFilter(Page: 2, PageSize: 20, Sort: "title"), Names);
        Assert.AreEqual(3, second.PageCount);
        Assert.AreEqual(20, second.Items.Count);
        Assert.AreEqual("T21", second.Items[0].Title);
        Assert.IsTrue(second.HasPrevious && second.HasNext);

        var beyond = await QueryAsync(rows, new AdminRequestFilter(Page: 99, PageSize: 20, Sort: "title"), Names);
        Assert.AreEqual(3, beyond.Page);
        Assert.AreEqual(5, beyond.Items.Count);
        Assert.IsFalse(beyond.HasNext);

        var custom = await QueryAsync(rows, new AdminRequestFilter(PageSize: 37, Sort: "title"), Names);
        Assert.AreEqual(37, custom.PageSize);
        Assert.AreEqual(2, custom.PageCount);

        var none = await QueryAsync([], new AdminRequestFilter(Page: 4), Names);
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
    public async Task SortOrdersTheFilteredRowsAndRemainsInTheNormalizedFilter()
    {
        var rows = new[]
        {
            Row("Zebra", AcquisitionRequestStatus.Pending),
            Row("Alpha", AcquisitionRequestStatus.Pending),
            Row("Middle", AcquisitionRequestStatus.Pending)
        };

        var page = await QueryAsync(rows, new AdminRequestFilter(Sort: "title"), Names);

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

    private static async Task<AdminRequestPage> QueryAsync(IReadOnlyList<AcquisitionRequest> rows, AdminRequestFilter filter, IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<Guid, IReadOnlyList<int>>? videoSeasons = null)
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        foreach (var row in rows)
        {
            if (VideoRequestPayload.Parse(row.PayloadJson) is { WorkId: > 0 } payload && row.Kind == MediaAcquisitionKind.Tv)
            {
                var workId = payload.WorkId;
                fixture.Db.Works.Add(new Work { Id = workId, CanonicalTitle = row.Title, MediaType = WorkMediaType.Series });
                foreach (var (id, number) in (payload.Requested?.SeasonIds ?? []).Zip(videoSeasons?.GetValueOrDefault(row.Id) ?? []))
                {
                    fixture.Db.WorkSeasons.Add(new WorkSeason { Id = id, WorkId = workId, SeasonNumber = number });
                }

                await fixture.Db.SaveChangesAsync();
            }

            var created = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(row.Kind, row.Provider, row.ExternalId, row.Title, row.Subtitle, null, row.PayloadJson), row.RequestedByProfileId, row.Status, null, CancellationToken.None);
            await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AcquisitionRequests\" SET \"Id\" = {row.Id.ToString()}, \"CreatedAt\" = {row.CreatedAt.ToString("O")} WHERE \"Id\" = {created.Id.ToString()}");
        }

        return (await fixture.Store.ReadQueueAsync(filter, Enum.GetValues<MediaAcquisitionKind>(), names, CancellationToken.None)).Page;
    }

    [TestMethod]
    public async Task QueuePagesAndFiltersBeyondTwoThousandRowsWithoutTruncation()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var seed = await fixture.Store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "seed", "Seed", null, null), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AcquisitionRequests" ("Id", "Kind", "Provider", "ExternalId", "Title", "RequestedByProfileId", "Status", "CreatedAt", "UpdatedAt")
            SELECT md5(('queue-test-' || n)::text)::uuid::text, 'book', 'test', n::text, CASE WHEN n = 2500 THEN 'Needle outside initial subset' ELSE 'Title ' || n END,
                'alice', 'pending', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'
            FROM generate_series(1, 2500) n;
            """);
        var queue = await fixture.Store.ReadQueueAsync(new AdminRequestFilter(Page: 999, PageSize: 20), [MediaAcquisitionKind.Book], Names, CancellationToken.None);
        Assert.AreEqual(2501, queue.Page.Total);
        Assert.AreEqual(126, queue.Page.Filter.Page);
        Assert.AreEqual(1, queue.Page.Items.Count);
        Assert.AreEqual(2501, queue.Page.TabCounts[AdminRequestTab.Open]);
        var found = await fixture.Store.ReadQueueAsync(new AdminRequestFilter(Search: "Needle"), [MediaAcquisitionKind.Book], Names, CancellationToken.None);
        Assert.AreEqual(1, found.Page.Total);
        Assert.AreEqual("Needle outside initial subset", found.Page.Items.Single().Title);
        Assert.AreNotEqual(seed.Id, found.Page.Items.Single().Id);
        var disabled = await fixture.Store.ReadQueueAsync(new AdminRequestFilter(), [MediaAcquisitionKind.Movie], Names, CancellationToken.None);
        Assert.IsFalse(disabled.AnyRequests);
        Assert.AreEqual(0, disabled.Page.Total);
    }
}
