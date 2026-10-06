using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using RequestsIndexModel = Jularr.Web.Pages.Requests.IndexModel;

namespace Jularr.Tests;

/// <summary>#597: the per-user request history query and the /Requests page on top of it.</summary>
[TestClass]
public sealed class RequestHistoryTests
{
    [TestMethod]
    public async Task HistoryHoldsOnlyTheProfilesOwnRequestsMostRecentChangeFirst()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = fixture.Store;
        var first = await store.CreateAsync(Draft("first"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await Task.Delay(25);
        await store.CreateAsync(Draft("second"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await Task.Delay(25);
        await store.CreateAsync(Draft("bobs"), "bob", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await store.CreateAsync(Draft("owners"), "owner", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None);
        await Task.Delay(25);
        // The first request changes last, so it moves to the top.
        await store.UpdateStatusAsync(first.Id, AcquisitionRequestStatus.Downloading, "release", Guid.NewGuid(), null, "owner", CancellationToken.None);

        var alice = await new RequestHistoryQuery(store).GetAsync("alice", RequestHistoryFilter.All, 1, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "first", "second" }, alice.Items.Select(request => request.ExternalId).ToArray());
        Assert.IsTrue(alice.Items.All(request => request.RequestedByProfileId == "alice"));

        var owner = await new RequestHistoryQuery(store).GetAsync("owner", RequestHistoryFilter.All, 1, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "owners" }, owner.Items.Select(request => request.ExternalId).ToArray(), "The owner's history is their own requests, not everyone's.");

        var nobody = await new RequestHistoryQuery(store).GetAsync("carol", RequestHistoryFilter.All, 1, CancellationToken.None);
        Assert.AreEqual(0, nobody.Items.Count);
        Assert.AreEqual(0, nobody.Total);
    }

    [TestMethod]
    public async Task FiltersSplitOpenFromFinishedRequestsAndCountThem()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = fixture.Store;
        foreach (var (id, status) in new[]
                 {
                     ("waiting", AcquisitionRequestStatus.Pending),
                     ("approved", AcquisitionRequestStatus.Approved),
                     ("downloading", AcquisitionRequestStatus.Downloading),
                     ("done", AcquisitionRequestStatus.Completed),
                     ("no", AcquisitionRequestStatus.Rejected),
                     ("broken", AcquisitionRequestStatus.Failed)
                 })
        {
            await store.CreateAsync(Draft(id), "alice", status, null, CancellationToken.None);
        }

        var query = new RequestHistoryQuery(store);
        var open = await query.GetAsync("alice", RequestHistoryFilter.Open, 1, CancellationToken.None);
        var finished = await query.GetAsync("alice", RequestHistoryFilter.Finished, 1, CancellationToken.None);
        var all = await query.GetAsync("alice", RequestHistoryFilter.All, 1, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "waiting", "approved", "downloading" }, open.Items.Select(request => request.ExternalId).ToArray());
        CollectionAssert.AreEquivalent(new[] { "done", "no", "broken" }, finished.Items.Select(request => request.ExternalId).ToArray());
        Assert.AreEqual(6, all.Items.Count);
        Assert.AreEqual(3, all.OpenCount);
        Assert.AreEqual(3, all.FinishedCount);
        Assert.AreEqual(3, open.Total);
        Assert.AreEqual(3, finished.Total);
        Assert.AreEqual(6, all.Total);
    }

    [TestMethod]
    public async Task HistoryIsPagedAndAPageOutOfRangeShowsTheNearestOne()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = fixture.Store;
        for (var index = 0; index < RequestHistoryQuery.PageSize + 2; index++)
        {
            await store.CreateAsync(Draft($"title-{index:00}"), "alice", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None);
        }

        var query = new RequestHistoryQuery(store);
        var first = await query.GetAsync("alice", RequestHistoryFilter.All, 1, CancellationToken.None);
        var second = await query.GetAsync("alice", RequestHistoryFilter.All, 2, CancellationToken.None);
        var beyond = await query.GetAsync("alice", RequestHistoryFilter.All, 9, CancellationToken.None);
        var before = await query.GetAsync("alice", RequestHistoryFilter.All, 0, CancellationToken.None);

        Assert.AreEqual(RequestHistoryQuery.PageSize, first.Items.Count);
        Assert.AreEqual(2, second.Items.Count);
        Assert.AreEqual(2, first.PageCount);
        Assert.IsTrue(first.HasNext);
        Assert.IsFalse(first.HasPrevious);
        Assert.IsFalse(second.HasNext);
        Assert.IsTrue(second.HasPrevious);
        Assert.AreEqual(0, first.Items.Select(request => request.Id).Intersect(second.Items.Select(request => request.Id)).Count(), "Pages do not overlap.");
        Assert.AreEqual(2, beyond.Page);
        Assert.AreEqual(1, before.Page);
    }

    [TestMethod]
    [DataRow(null, RequestHistoryFilter.All)]
    [DataRow("", RequestHistoryFilter.All)]
    [DataRow("open", RequestHistoryFilter.Open)]
    [DataRow("FINISHED", RequestHistoryFilter.Finished)]
    [DataRow("nonsense", RequestHistoryFilter.All)]
    public void FilterNamesRoundTrip(string? value, RequestHistoryFilter expected)
    {
        var parsed = RequestHistoryQuery.ParseFilter(value);

        Assert.AreEqual(expected, parsed);
        Assert.AreEqual(parsed, RequestHistoryQuery.ParseFilter(RequestHistoryQuery.FilterName(parsed)));
    }

    [TestMethod]
    public async Task HistoryPageShowsTheSignedInProfilesRequestsWithTheirConsumerState()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = fixture.Store;
        await store.CreateAsync(Draft("mine"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await store.CreateAsync(Draft("theirs"), "bob", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        var cancelled = await store.CreateAsync(Draft("cancelled"), "alice", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await fixture.Service("alice", AccountRole.User).CancelAsync(cancelled.Id, CancellationToken.None);

        var page = Page(fixture, "alice");
        await page.OnGetAsync("open", 1, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "mine" }, page.History.Items.Select(request => request.ExternalId).ToArray());
        Assert.AreEqual(RequestHistoryFilter.Open, page.History.Filter);
        Assert.AreEqual(ConsumerAcquisitionState.WaitingForApproval, page.Rows.Single().State.State);

        await page.OnGetAsync("finished", 1, CancellationToken.None);
        Assert.IsTrue(page.Rows.Single().Request.IsCancelled, "A cancelled request is finished and says so.");
    }

    [TestMethod]
    public void HistoryPageIsForAnySignedInUser()
    {
        var type = typeof(RequestsIndexModel);
        var restricted = type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Any(attribute => attribute.Policy is not null || !string.IsNullOrEmpty(attribute.Roles));

        Assert.IsFalse(restricted, "Everyone with an account can see their own requests.");
        Assert.AreEqual(0, type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true).Length);
    }

    private static RequestsIndexModel Page(AcquisitionAccessFixture fixture, string profileId)
    {
        var account = AcquisitionAccessFixture.Account(profileId, AccountRole.User);
        var profiles = new QualityProfileStore(new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"jularr-profiles-{Guid.NewGuid():N}")));
        var page = new RequestsIndexModel(
            fixture.Db,
            account,
            new RequestHistoryQuery(fixture.Store),
            fixture.StatusQuery(),
            profiles,
            TimeProvider.System);
        page.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext { User = AcquisitionAccessFixture.Principal(profileId, AccountRole.User) },
            ViewData = new ViewDataDictionary<RequestsIndexModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        };
        return page;
    }

    private static AcquisitionRequestDraft Draft(string id) =>
        new(MediaAcquisitionKind.Anime, "anilist", id, $"Title {id}", null, null);
}
