using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

[TestClass]
public sealed class AcquisitionAccessTests
{
    [TestMethod]
    public void OwnerAlwaysAutoApprovesAndAddsManually()
    {
        var capabilities = AcquisitionCapabilities.Resolve(
            MediaAcquisitionKind.Book,
            MediaCapability.Instant,
            ManualAddMode.OwnerOnly,
            isOwner: true);

        Assert.IsTrue(capabilities.CanRequest);
        Assert.IsTrue(capabilities.AutoApproves);
        Assert.IsTrue(capabilities.CanAddManually);
    }

    [TestMethod]
    [DataRow(MediaCapability.Hidden, false, false)]
    [DataRow(MediaCapability.Browse, false, false)]
    [DataRow(MediaCapability.Request, true, false)]
    [DataRow(MediaCapability.Instant, true, true)]
    public void ProfileCapabilitiesFollowTheCapabilityMatrix(MediaCapability capability, bool canRequest, bool autoApproves)
    {
        var capabilities = AcquisitionCapabilities.Resolve(
            MediaAcquisitionKind.Anime,
            capability,
            ManualAddMode.Users,
            isOwner: false);

        Assert.AreEqual(canRequest, capabilities.CanRequest);
        Assert.AreEqual(autoApproves, capabilities.AutoApproves);
        Assert.IsTrue(capabilities.CanAddManually);
    }

    [TestMethod]
    public async Task DefaultsAreOwnerOnlyManualAndPoliciesPersist()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = new AcquisitionAccessStore(fixture.Db);

        var defaults = await store.GetPoliciesAsync(CancellationToken.None);
        Assert.AreEqual(Enum.GetValues<MediaAcquisitionKind>().Length, defaults.Count);
        Assert.IsTrue(defaults.All(policy => policy.Manual == ManualAddMode.OwnerOnly));

        await store.SavePolicyAsync(
            new AcquisitionAccessPolicy(MediaAcquisitionKind.Manga, ManualAddMode.Users),
            CancellationToken.None);

        var manga = await store.GetPolicyAsync(MediaAcquisitionKind.Manga, CancellationToken.None);
        Assert.AreEqual(ManualAddMode.Users, manga.Manual);
        Assert.AreEqual(ManualAddMode.OwnerOnly, (await store.GetPolicyAsync(MediaAcquisitionKind.Book, CancellationToken.None)).Manual);
    }

    [TestMethod]
    public async Task UserRequestWaitsForOwnerApprovalThenRunsTheExecutor()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var user = fixture.Service("alice", isOwner: false, executor);
        var owner = fixture.Service("owner", isOwner: true, executor);

        var request = await user.SubmitAsync(Draft("dune"), CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Pending, request.Status);
        Assert.AreEqual(0, executor.Runs);

        var again = await user.SubmitAsync(Draft("dune"), CancellationToken.None);
        Assert.AreEqual(request.Id, again.Id, "An open request is reused, not duplicated.");

        var approved = await owner.ApproveAsync(request.Id, CancellationToken.None);
        Assert.AreEqual(1, executor.Runs);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, approved.Status);
        Assert.AreEqual("owner", approved.DecidedByProfileId);
        Assert.IsNotNull(approved.OperationId);
    }

    [TestMethod]
    public async Task AutomaticUsersAndTheOwnerStartAcquisitionImmediately()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Book, MediaCapability.Instant);
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);

        var request = await fixture.Service("alice", isOwner: false, executor).SubmitAsync(Draft("dune"), CancellationToken.None);

        Assert.AreEqual(1, executor.Runs);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
    }

    [TestMethod]
    public async Task UsersBelowRequestAreRefusedAndCannotDecide()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Anime, MediaCapability.Browse);
        var user = fixture.Service("alice", isOwner: false, new RecordingExecutor(MediaAcquisitionKind.Anime));

        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(
            () => user.SubmitAsync(Draft("frieren", MediaAcquisitionKind.Anime), CancellationToken.None));

        var bookRequest = await user.SubmitAsync(Draft("dune"), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(
            () => user.ApproveAsync(bookRequest.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task MediaWithoutExecutorStaysApprovedForTheOwnerAndFailuresAreRecorded()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var owner = fixture.Service("owner", isOwner: true, new RecordingExecutor(MediaAcquisitionKind.Book, fail: true));

        var manga = await owner.SubmitAsync(Draft("berserk", MediaAcquisitionKind.Manga), CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, manga.Status);

        var book = await owner.SubmitAsync(Draft("dune"), CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, book.Status);
        Assert.AreEqual("indexer down", book.StatusMessage);

        await owner.MarkCompletedAsync(manga.Id, CancellationToken.None);
        Assert.AreEqual(
            AcquisitionRequestStatus.Completed,
            (await new AcquisitionAccessStore(fixture.Db).GetAsync(manga.Id, CancellationToken.None))!.Status);
    }

    [TestMethod]
    public async Task RequesterCanWithdrawOwnPendingRequestOnly()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var alice = fixture.Service("alice", isOwner: false, executor);
        var bob = fixture.Service("bob", isOwner: false, executor);

        var request = await alice.SubmitAsync(Draft("dune"), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => bob.CancelAsync(request.Id, CancellationToken.None));

        await alice.CancelAsync(request.Id, CancellationToken.None);
        var stored = await new AcquisitionAccessStore(fixture.Db).GetAsync(request.Id, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Rejected, stored!.Status);
    }

    [TestMethod]
    public void BookReleaseSelectorPrefersMatchingEpubThenAcceptsPdf()
    {
        ProwlarrReleaseCandidate Release(string title, string protocol = "usenet", long size = 5_000_000) =>
            new(title, "idx", 1, protocol, size, null, null, DateTimeOffset.UtcNow, 1, 1, Guid.NewGuid().ToString(), null,
                Jularr.Web.Features.Acquisition.Release.ReleaseParser.Parse(title), [], new Uri("https://indexer.example/get/" + Guid.NewGuid()), null);

        var releases = new[]
        {
            Release("Frank Herbert - Dune (1965) PDF"),
            Release("Frank Herbert - Dune Messiah EPUB"),
            Release("Frank Herbert - Dune (1965) retail EPUB"),
            Release("Frank Herbert - Dune EPUB", protocol: "torrent"),
            Release("Some Other Book EPUB"),
            Release("Frank Herbert - Dune MOBI")
        };

        var best = BookReleaseSelector.Pick(releases, "Dune", "Frank Herbert");

        Assert.IsNotNull(best);
        Assert.AreEqual("Frank Herbert - Dune (1965) retail EPUB", best.Title);
        Assert.AreEqual("Frank Herbert - Dune (1965) PDF", BookReleaseSelector.Pick([releases[0], releases[3], releases[5]], "Dune", "Frank Herbert")?.Title,
            "Without an EPUB the PDF is taken; torrent and MOBI results never are.");
        Assert.IsNull(BookReleaseSelector.Pick([releases[3], releases[5]], "Dune", "Frank Herbert"));
    }

    [TestMethod]
    public void BookReleaseSelectorRanksEpubOverPdfOverUnknownFormat()
    {
        ProwlarrReleaseCandidate Release(string title) =>
            new(title, "idx", 1, "usenet", 3_000_000, null, null, DateTimeOffset.UtcNow, 1, 1, Guid.NewGuid().ToString(), null,
                Jularr.Web.Features.Acquisition.Release.ReleaseParser.Parse(title), [], new Uri("https://indexer.example/get/" + Guid.NewGuid()), null);

        var ranked = BookReleaseSelector.Rank(
            [Release("James Clear - Atomic Habits"), Release("James Clear - Atomic Habits PDF"), Release("James Clear - Atomic Habits EPUB")],
            "Atomic Habits",
            "James Clear");

        CollectionAssert.AreEqual(
            new[] { "James Clear - Atomic Habits EPUB", "James Clear - Atomic Habits PDF", "James Clear - Atomic Habits" },
            ranked.Select(release => release.Release.Title).ToArray());
        Assert.IsTrue(ranked.All(release => release.Score > 0), "A name without a format is allowed; the download import checks it.");
    }

    [TestMethod]
    public void BookReleaseSelectorIgnoresSubtitlesAndExplainsRejections()
    {
        ProwlarrReleaseCandidate Release(string title, string protocol = "usenet") =>
            new(title, "idx", 1, protocol, 2_000_000, null, null, DateTimeOffset.UtcNow, 1, 1, Guid.NewGuid().ToString(), null,
                Jularr.Web.Features.Acquisition.Release.ReleaseParser.Parse(title), [], new Uri("https://indexer.example/get/" + Guid.NewGuid()), null);

        var ranked = BookReleaseSelector.Rank(
            [
                Release("Mary Shelley - Frankenstein AZW3"),
                Release("Mary Shelley - Frankenstein EPUB"),
                Release("Frankenstein EPUB", protocol: "torrent"),
                Release("Dracula EPUB")
            ],
            "Frankenstein; or, The Modern Prometheus",
            "Mary Shelley");

        Assert.AreEqual("Mary Shelley - Frankenstein EPUB", ranked[0].Release.Title, "The subtitle is not required in the release name.");
        Assert.IsNull(ranked[0].RejectedBecause);
        Assert.AreEqual(1, ranked.Count(release => release.Score > 0));
        CollectionAssert.AreEquivalent(
            new[] { "AZW3, not EPUB or PDF", "not a Usenet release", "title does not match" },
            ranked.Where(release => release.Score == 0).Select(release => release.RejectedBecause).ToArray());
    }

    [TestMethod]
    public void BookReleaseSelectorAppliesSharedProfileOnlyAfterIdentityMatch()
    {
        ProwlarrReleaseCandidate Release(string title) =>
            new(
                title,
                "idx",
                1,
                "usenet",
                3_000_000,
                null,
                null,
                DateTimeOffset.UtcNow,
                1,
                1,
                Guid.NewGuid().ToString(),
                null,
                Jularr.Web.Features.Acquisition.Release.ReleaseParser.Parse(title),
                [],
                new Uri("https://indexer.example/get/" + Guid.NewGuid()),
                null);

        var profile = BookQualityProfiles.CreateDefaultBook() with
        {
            MustContain = ["retail"]
        };
        var ranked = BookReleaseSelector.Rank(
            [
                Release("Frank Herbert - Dune EPUB"),
                Release("Some Other Book retail EPUB"),
                Release("Frank Herbert - Dune retail EPUB")
            ],
            "Dune",
            "Frank Herbert",
            profile);

        Assert.AreEqual(
            "Frank Herbert - Dune retail EPUB",
            ranked[0].Release.Title);
        Assert.IsTrue(ranked[0].Score > 0);
        Assert.AreEqual(
            "Missing required term 'retail'.",
            ranked.Single(candidate =>
                candidate.Release.Title == "Frank Herbert - Dune EPUB")
                .RejectedBecause);
        Assert.AreEqual(
            "title does not match",
            ranked.Single(candidate =>
                candidate.Release.Title == "Some Other Book retail EPUB")
                .RejectedBecause,
            "A quality-rule match can never override a wrong Book identity.");
    }

    [TestMethod]
    public void BookManualSearchOnlySelectsFreshAcceptedUntriedIdentity()
    {
        ProwlarrReleaseCandidate Release(string title, string guid, string protocol = "usenet") =>
            new(
                title,
                "idx",
                1,
                protocol,
                3_000_000,
                null,
                null,
                DateTimeOffset.UtcNow,
                1,
                1,
                guid,
                null,
                Jularr.Web.Features.Acquisition.Release.ReleaseParser.Parse(title),
                [],
                new Uri("https://indexer.example/get/" + guid),
                null);

        var accepted = Release(
            "Frank Herbert - Dune retail EPUB",
            "accepted");
        var rejected = Release(
            "Frank Herbert - Dune MOBI",
            "rejected");
        var result = new BookUsenetSearchResult(
            ["Frank Herbert Dune"],
            BookReleaseSelector.Rank(
                [accepted, rejected],
                "Dune",
                "Frank Herbert"),
            [],
            false);

        Assert.AreSame(
            accepted,
            BookManualSearchService.SelectRelease(
                result,
                accepted.Identity)!.Release);
        Assert.IsNull(
            BookManualSearchService.SelectRelease(
                result,
                rejected.Identity),
            "A rejected candidate can never be manually grabbed.");
        Assert.IsNull(
            BookManualSearchService.SelectRelease(
                result,
                "prowlarr:1:https://attacker.example/evil.nzb"),
            "The POSTed value is only an opaque identity, never a trusted URL.");
        Assert.IsNull(
            BookManualSearchService.SelectRelease(
                result,
                accepted.Identity,
                [accepted.Title]),
            "An already tried release is not submitted again.");
    }

    [TestMethod]
    public void BookUsenetQueriesUseAuthorAndMainTitleFirst()
    {
        static string[] Texts(string title, string? author) =>
            [.. SearchPlanner.Plan(new SearchIntent(MediaAcquisitionKind.Book, title.Trim()) { Creator = author }, null, SearchDepth.Normal).Where(query => !query.AnyCategory).Select(query => query.Text!)];

        CollectionAssert.AreEqual(new[] { "Mary Shelley Frankenstein", "Frankenstein", "Frankenstein: The 1818 Text" }, Texts("Frankenstein: The 1818 Text", "Mary Shelley"));
        CollectionAssert.AreEqual(new[] { "Dune" }, Texts(" Dune ", null));
        Assert.AreEqual("Dune", SearchPlanner.MainTitle("Dune - Deluxe Edition"));
    }

    [TestMethod]
    public async Task WaitingBookRequestsAreSearchedAgainOnlyWhenDue()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = new AcquisitionAccessStore(fixture.Db);
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var owner = fixture.Service("owner", isOwner: true, executor);
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        var due = await store.CreateAsync(BookDraft("dune", now.AddMinutes(-1)), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var later = await store.CreateAsync(BookDraft("emma", now.AddHours(3)), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        var handler = new BookWantedRequestHandler(store, owner);

        Assert.IsTrue(handler.IsSearchDue(due, now));
        Assert.IsFalse(handler.IsSearchDue(later, now));
        Assert.IsTrue(handler.IsSearchDue(later, now.AddHours(3)));
        Assert.AreEqual(0, executor.Runs, "Wanted decides when to search; the handler only answers whether it is due.");
    }

    [TestMethod]
    public async Task FailedBookDownloadContinuesTheRequestWithTheNextRelease()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = new AcquisitionAccessStore(fixture.Db);
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var owner = fixture.Service("owner", isOwner: true, executor);

        var request = await owner.SubmitAsync(Draft("dune"), CancellationToken.None);
        var failedOperation = request.OperationId!.Value;
        Assert.AreEqual(1, executor.Runs);

        await new BookWantedRequestHandler(store, owner).ContinueAfterProblemAsync(
            request,
            "The download failed: Repair failed.",
            CancellationToken.None);
        Assert.AreEqual(2, executor.Runs);
        var continued = (await store.GetAsync(request.Id, CancellationToken.None))!;
        Assert.AreNotEqual(failedOperation, continued.OperationId);
        Assert.AreEqual("The download failed: Repair failed.", BookAcquisitionExecutor.ReadPayload(continued).LastProblem);
    }

    [TestMethod]
    public void BookSearchBackoffGrowsToDaily()
    {
        Assert.AreEqual(TimeSpan.FromHours(6), ReleaseRequestTracker.SearchBackoff(1));
        Assert.AreEqual(TimeSpan.FromHours(12), ReleaseRequestTracker.SearchBackoff(2));
        Assert.AreEqual(TimeSpan.FromHours(24), ReleaseRequestTracker.SearchBackoff(7));
    }

    private static AcquisitionRequestDraft BookDraft(string id, DateTime nextSearchUtc) =>
        new(MediaAcquisitionKind.Book, "test", id, id.ToUpperInvariant(), "Author", null,
            System.Text.Json.JsonSerializer.Serialize(
                new BookRequestPayload(id, id, "Author") { NextSearchUtc = nextSearchUtc },
                System.Text.Json.JsonSerializerOptions.Web));

    private static AcquisitionRequestDraft Draft(string id, MediaAcquisitionKind kind = MediaAcquisitionKind.Book) =>
        new(kind, "test", id, id.ToUpperInvariant(), "Author", null);
}
