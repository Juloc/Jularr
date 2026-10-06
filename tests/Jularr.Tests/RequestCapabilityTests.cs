using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

/// <summary>#597 on top of #436: what a profile may do with a media type decides request versus instant.</summary>
[TestClass]
public sealed class RequestCapabilityTests
{
    [TestMethod]
    public async Task RequestCapabilityCreatesAPendingRequestAndInstantCapabilityAddsAtOnce()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var alice = fixture.Service("alice", AccountRole.User, executor);

        var requested = await alice.SubmitAsync(Draft("dune"), CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Pending, requested.Status);
        Assert.AreEqual(0, executor.Runs, "A request waits for a decision before anything is acquired.");

        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Book, MediaCapability.Instant);
        var instant = await alice.SubmitAsync(Draft("emma"), CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, instant.Status);
        Assert.AreEqual(1, executor.Runs);
        Assert.AreEqual("alice", instant.DecidedByProfileId, "Adding instantly is the profile's own decision.");
        Assert.IsFalse(instant.WasAutoApproved);
    }

    [TestMethod]
    [DataRow(MediaCapability.Hidden)]
    [DataRow(MediaCapability.Browse)]
    public async Task ProfilesBelowRequestCannotSubmitAnything(MediaCapability capability)
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Anime, capability);
        var alice = fixture.Service("alice", AccountRole.User, new RecordingExecutor(MediaAcquisitionKind.Anime));

        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(
            () => alice.SubmitAsync(Draft("frieren", MediaAcquisitionKind.Anime), CancellationToken.None));

        var access = await alice.GetCapabilitiesAsync(MediaAcquisitionKind.Anime, CancellationToken.None);
        Assert.IsFalse(access.CanRequest);
        Assert.AreEqual(0, (await fixture.Store.ListAsync(null, null, openOnly: false, 10, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task TheCapabilityIsPerMediaTypeAndAPersonalOverrideWinsOverTheRoleDefault()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Manga, MediaCapability.Browse);
        await fixture.Capabilities.SetUserOverrideAsync("alice", WorkMediaType.Anime, MediaCapability.Instant);
        var anime = new RecordingExecutor(MediaAcquisitionKind.Anime);
        var alice = fixture.Service("alice", AccountRole.User, anime, new RecordingExecutor(MediaAcquisitionKind.Book));
        var bob = fixture.Service("bob", AccountRole.User, anime);

        var animeAccess = await alice.GetCapabilitiesAsync(MediaAcquisitionKind.Anime, CancellationToken.None);
        var mangaAccess = await alice.GetCapabilitiesAsync(MediaAcquisitionKind.Manga, CancellationToken.None);
        var bookAccess = await alice.GetCapabilitiesAsync(MediaAcquisitionKind.Book, CancellationToken.None);
        Assert.IsTrue(animeAccess is { CanRequest: true, AutoApproves: true });
        Assert.IsFalse(mangaAccess.CanRequest);
        Assert.IsTrue(bookAccess is { CanRequest: true, AutoApproves: false });

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await alice.SubmitAsync(Draft("a", MediaAcquisitionKind.Anime), CancellationToken.None)).Status);
        Assert.AreEqual(
            AcquisitionRequestStatus.Pending,
            (await bob.SubmitAsync(Draft("b", MediaAcquisitionKind.Anime), CancellationToken.None)).Status,
            "Bob has no override and keeps the role default.");
    }

    [TestMethod]
    public async Task MediaManagersAndTheOwnerAddInstantlyByDefault()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);

        var manager = await fixture.Service("mia", AccountRole.MediaManager, executor).SubmitAsync(Draft("one"), CancellationToken.None);
        var owner = await fixture.Service("owner", AccountRole.Owner, executor).SubmitAsync(Draft("two"), CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, manager.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, owner.Status);
        Assert.AreEqual(2, executor.Runs);
    }

    [TestMethod]
    public async Task CapabilitiesExposeTheManualRuleSeparatelyFromTheCapability()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);

        var before = await alice.GetCapabilitiesAsync(MediaAcquisitionKind.Book, CancellationToken.None);
        await fixture.Store.SavePolicyAsync(new AcquisitionAccessPolicy(MediaAcquisitionKind.Book, ManualAddMode.Users), CancellationToken.None);
        var after = await alice.GetCapabilitiesAsync(MediaAcquisitionKind.Book, CancellationToken.None);

        Assert.IsFalse(before.CanAddManually);
        Assert.IsTrue(after.CanAddManually);
        Assert.AreEqual(before.AutoApproves, after.AutoApproves, "The manual rule does not change request versus instant.");
    }

    [TestMethod]
    public async Task DecisionNotificationsOpenTheRequestsStatus()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var alice = fixture.Service("alice", AccountRole.User);
        var owner = fixture.Service("owner", AccountRole.Owner);

        var request = await alice.SubmitAsync(Draft("dune"), CancellationToken.None);
        await owner.RejectAsync(request.Id, "Not now.", CancellationToken.None);

        var denied = fixture.Events.Published.Single(item => item.Category == Jularr.Web.Features.Events.JularrEventCategory.RequestDenied);
        Assert.AreEqual(AcquisitionRequestService.StatusPath(request.Id), denied.DeepLink);
    }

    private static AcquisitionRequestDraft Draft(string id, MediaAcquisitionKind kind = MediaAcquisitionKind.Book) =>
        new(kind, "test", id, id.ToUpperInvariant(), "Author", null);
}
