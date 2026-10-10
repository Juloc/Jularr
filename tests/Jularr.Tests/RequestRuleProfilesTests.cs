using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Instance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class RequestRuleProfilesTests
{
    [TestMethod]
    public async Task Initialization_PersistsFreshDefault_AndPreservesLegacyApprovalSemantics()
    {
        var directory = Directory.CreateTempSubdirectory("jularr-rule-settings-");
        try
        {
            var store = new AcquisitionRequestSettingsStore(directory.FullName);
            await store.InitializeAsync(CancellationToken.None);
            var file = Path.Combine(directory.FullName, "acquisition", AcquisitionRequestSettingsStore.FileName);
            Assert.IsTrue(File.Exists(file));
            var fresh = await new AcquisitionRequestSettingsStore(directory.FullName).LoadAsync();
            Assert.AreEqual(10, fresh.Rules.Profiles.Single().Values.Limit);
            Assert.IsFalse(fresh.Rules.HasApprovalTransition);
            await File.WriteAllTextAsync(file, """
                {"autoApprovalRules":[{"id":"trusted","name":"Trusted","enabled":true,"kinds":["book"],"profileIds":["alice"],"quota":{"maxRequests":3,"periodDays":7}}],"requesterQualityProfileIds":["existing-quality"]}
                """);
            await store.InitializeAsync(CancellationToken.None);
            var legacy = await store.LoadAsync();
            Assert.IsNull(legacy.Rules.Profiles.Single().Values.Limit, "An approval quota must not become a new submission limit during upgrade.");
            Assert.AreEqual(RequestApprovalMode.Manual, legacy.Rules.Profiles.Single().Values.Approval);
            Assert.AreEqual(1, legacy.Rules.Resolve("alice", legacy.AutoApprovalRules).TransitionRules.Count);
            Assert.AreEqual("existing-quality", legacy.RequesterQualityProfileIds.Single());
            Assert.AreEqual("existing-quality", legacy.Rules.Resolve("alice", legacy.AutoApprovalRules).Values.QualityProfileIds!.Single());
            legacy = await store.FinishApprovalTransitionAsync(legacy.Revision, CancellationToken.None);
            Assert.AreEqual(0, legacy.Rules.Resolve("alice", legacy.AutoApprovalRules).TransitionRules.Count);
            Assert.AreEqual("trusted", legacy.AutoApprovalRules.Single().Id, "Retiring evaluation must preserve the saved legacy configuration.");
            var restored = await new AcquisitionRequestSettingsStore(directory.FullName).LoadAsync();
            Assert.IsFalse(restored.Rules.HasApprovalTransition);
            await File.WriteAllTextAsync(file, """
                {"requesterQualityProfileIds":["saved-quality"],"rules":{"defaultId":1,"nextId":3,"users":{},"profiles":[
                  {"id":1,"name":"Standard","values":{"limit":10,"periodDays":30,"approval":0,"kinds":[0]}},
                  {"id":2,"name":"Trusted","values":{"limit":20,"periodDays":7,"approval":0,"kinds":[0]}}
                ]}}
                """);
            await store.InitializeAsync(CancellationToken.None);
            var upgraded = await store.LoadAsync();
            Assert.IsTrue(upgraded.Rules.Profiles.All(profile => profile.Values.QualityProfileIds!.SequenceEqual(["saved-quality"])), "Existing rule profiles must retain the previous global quality choices.");
            await File.WriteAllTextAsync(file, "null");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync());
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public async Task Quota_RollingWindowAndUnlimited_UseCreatedUsage()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var settings = await fixture.Settings.LoadAsync();
        settings = await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Limit = 1, PeriodDays = 1 }, settings.Revision, CancellationToken.None);
        var service = fixture.Service("alice", AccountRole.User);
        var first = await service.SubmitAsync(Draft("old", MediaAcquisitionKind.Book), CancellationToken.None);
        var expired = DateTime.UtcNow.AddDays(-2);
        await fixture.Db.Database.ExecuteSqlAsync($"""UPDATE "RequestSubmissionUsage" SET "CreatedAt" = {expired} WHERE "RequestId" = {first.Id}""");
        await service.SubmitAsync(Draft("new", MediaAcquisitionKind.Manga), CancellationToken.None);
        var denied = await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => service.SubmitAsync(Draft("denied", MediaAcquisitionKind.Book), CancellationToken.None));
        Assert.AreEqual("requestRules.quotaReached", denied.MessageKey);
        await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Limit = null }, settings.Revision, CancellationToken.None);
        await service.SubmitAsync(Draft("unlimited-one", MediaAcquisitionKind.Book), CancellationToken.None);
        await service.SubmitAsync(Draft("unlimited-two", MediaAcquisitionKind.Manga), CancellationToken.None);
    }

    [TestMethod]
    public void Editor_RejectsForbiddenKinds_AndRetainsUneditableInheritance()
    {
        var input = new RequestRuleEditorInput { Kinds = ["book"], Limit = 5 };
        var edited = input.ReadValues([MediaAcquisitionKind.Book], [MediaAcquisitionKind.Book, MediaAcquisitionKind.Movie]);
        var difference = RequestRuleOverrides.Difference(edited, RequestRuleValues.Standard with { Kinds = [MediaAcquisitionKind.Book, MediaAcquisitionKind.Movie] });
        Assert.IsTrue(difference.HasLimit);
        Assert.IsNull(difference.Kinds, "Editing only the quota must not turn a hidden/disabled inherited type into a sparse override.");
        input.Kinds = ["movie"];
        Assert.ThrowsExactly<ArgumentException>(() => input.ReadValues([MediaAcquisitionKind.Book], []));
        Assert.ThrowsExactly<ArgumentException>(() => (RequestRuleValues.Standard with { PeriodDays = 0 }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (RequestRuleValues.Standard with { Approval = (RequestApprovalMode)99 }).Validate());
        input.Kinds = ["book"];
        input.QualityProfileIds = ["unknown"];
        Assert.ThrowsExactly<ArgumentException>(() => input.ReadValues([MediaAcquisitionKind.Book], [], ["known"]));
        input.QualityProfileIds = ["known"];
        CollectionAssert.AreEquivalent(new[] { "known", "retired" }, input.ReadValues([MediaAcquisitionKind.Book], [], ["known"], ["retired"]).QualityProfileIds!.ToArray());
    }

    [TestMethod]
    public async Task Profiles_DefaultAndAssignments_RemainConsistent()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var settings = await fixture.Settings.LoadAsync();
        Assert.AreEqual(1, settings.Rules.Profiles.Count);
        Assert.AreEqual(10, settings.Rules.Profiles[0].Values.Limit);
        var trusted = new RequestRuleValues(20, 30, RequestApprovalMode.Automatic, [MediaAcquisitionKind.Book, MediaAcquisitionKind.Manga]);
        settings = await fixture.Settings.SaveProfileAsync(null, "Trusted", null, trusted, settings.Revision, CancellationToken.None);
        var trustedId = settings.Rules.Profiles.Last().Id;
        settings = await fixture.Settings.SaveUserRuleAsync("alice", trustedId, trusted with { Limit = 5 }, true, settings.Revision, CancellationToken.None);
        Assert.IsTrue(settings.Rules.Users["alice"].Overrides.HasLimit);
        Assert.IsNull(settings.Rules.Users["alice"].Overrides.Approval);
        Assert.IsNull(settings.Rules.Users["alice"].Overrides.Kinds);
        settings = await fixture.Settings.SaveProfileAsync(trustedId, "Trusted", null, trusted with { PeriodDays = 7, Approval = RequestApprovalMode.Manual }, settings.Revision, CancellationToken.None);
        var effective = settings.Rules.Resolve("alice", []);
        Assert.AreEqual(5, effective.Values.Limit);
        Assert.AreEqual(7, effective.Values.PeriodDays);
        Assert.AreEqual(RequestApprovalMode.Manual, effective.Values.Approval);
        settings = await fixture.Settings.DuplicateProfileAsync(trustedId, "Family", settings.Revision, CancellationToken.None);
        var familyId = settings.Rules.Profiles.Last().Id;
        settings = await fixture.Settings.SetDefaultProfileAsync(familyId, settings.Revision, CancellationToken.None);
        Assert.AreEqual(familyId, settings.Rules.Resolve("bob", []).Profile.Id);
        Assert.AreEqual(trustedId, settings.Rules.Resolve("alice", []).Profile.Id);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Settings.DeleteProfileAsync(familyId, true, settings.Revision, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Settings.DeleteProfileAsync(trustedId, false, settings.Revision, CancellationToken.None));
        settings = await fixture.Settings.DeleteProfileAsync(trustedId, true, settings.Revision, CancellationToken.None);
        Assert.AreEqual(familyId, settings.Rules.Resolve("alice", []).Profile.Id);
        settings = await fixture.Settings.SaveUserRuleAsync("alice", null, trusted, false, settings.Revision, CancellationToken.None);
        Assert.IsFalse(settings.Rules.Users.ContainsKey("alice"));
    }

    [TestMethod]
    public async Task Profiles_ConcurrentEdits_RejectStaleRevision()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var settings = await fixture.Settings.LoadAsync();
        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(async index =>
        {
            try
            {
                await fixture.Settings.SaveProfileAsync(null, $"Rule {index}", null, RequestRuleValues.Standard, settings.Revision, CancellationToken.None);
                return true;
            }
            catch (RequestRuleConflictException)
            {
                return false;
            }
        }));
        Assert.AreEqual(1, results.Count(result => result));
        Assert.AreEqual(2, (await fixture.Settings.LoadAsync()).Rules.Profiles.Count);
    }

    [TestMethod]
    public async Task Quota_AllKindsAndDeletion_CountDurableSubmissionsOnly()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var settings = await fixture.Settings.LoadAsync();
        settings = await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Limit = 2 }, settings.Revision, CancellationToken.None);
        var service = fixture.Service("alice", AccountRole.User);
        var first = await service.SubmitAsync(Draft("one", MediaAcquisitionKind.Book), CancellationToken.None);
        var duplicate = await service.SubmitWithOutcomeAsync(Draft("one", MediaAcquisitionKind.Book), CancellationToken.None);
        Assert.IsTrue(duplicate.AlreadyRequested);
        Assert.AreEqual(first.Id, duplicate.Request.Id);
        await service.SubmitAsync(Draft("two", MediaAcquisitionKind.Manga), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => service.SubmitAsync(Draft("three", MediaAcquisitionKind.Book), CancellationToken.None));
        await fixture.Store.TryDeleteAsync(first, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => service.SubmitAsync(Draft("four", MediaAcquisitionKind.Book), CancellationToken.None));
        Assert.AreEqual(2, await fixture.Db.Database.SqlQuery<int>($"""SELECT COUNT(*)::integer AS "Value" FROM "RequestSubmissionUsage" WHERE "RequestedByProfileId" = {"alice"}""").SingleAsync());
    }

    [TestMethod]
    public async Task Quota_ConcurrentDifferentTitles_AdmitsOne()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var settings = await fixture.Settings.LoadAsync();
        await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Limit = 1 }, settings.Revision, CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(async index =>
        {
            await using var db = fixture.OpenContext();
            try
            {
                await new AcquisitionAccessStore(db).CreateAsync(Draft($"title-{index}", MediaAcquisitionKind.Book), "alice", AcquisitionRequestStatus.Pending, null,
                    CancellationToken.None, RequestRuleValues.Standard with { Limit = 1 });
                return true;
            }
            catch (AcquisitionAccessDeniedException)
            {
                return false;
            }
        }));
        Assert.AreEqual(1, results.Count(result => result));
    }

    [TestMethod]
    public async Task Quota_ConcurrentDuplicate_ReusesOneRequestWithoutChargingAgain()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var settings = await fixture.Settings.LoadAsync();
        await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Limit = 1 }, settings.Revision, CancellationToken.None);
        var submissions = await Task.WhenAll(Enumerable.Range(1, 3).Select(async _ =>
        {
            await using var db = fixture.OpenContext();
            var service = new AcquisitionRequestService(new AcquisitionAccessStore(db), [], AcquisitionAccessFixture.Account("alice", AccountRole.User),
                new MediaCapabilityService(fixture.Capabilities), fixture.Settings, fixture.Events, NullLogger<AcquisitionRequestService>.Instance);
            return await service.SubmitWithOutcomeAsync(Draft("same-title", MediaAcquisitionKind.Book), CancellationToken.None);
        }));
        Assert.AreEqual(1, submissions.Select(submission => submission.Request.Id).Distinct().Count());
        Assert.AreEqual(1, submissions.Count(submission => !submission.AlreadyRequested));
        Assert.AreEqual(1, await fixture.Db.Database.SqlQuery<int>($"""SELECT COUNT(*)::integer AS "Value" FROM "RequestSubmissionUsage" WHERE "RequestedByProfileId" = {"alice"}""").SingleAsync());
    }

    [TestMethod]
    public async Task Approval_ProfileNeverWidensCapabilities_AndInstantIsPreserved()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var settings = await fixture.Settings.LoadAsync();
        await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Approval = RequestApprovalMode.Automatic }, settings.Revision, CancellationToken.None);
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Book, MediaCapability.Browse);
        var service = fixture.Service("alice", AccountRole.User);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => service.SubmitAsync(Draft("denied", MediaAcquisitionKind.Book), CancellationToken.None));
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Book, MediaCapability.Request);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await service.SubmitAsync(Draft("allowed", MediaAcquisitionKind.Book), CancellationToken.None)).Status);
        settings = await fixture.Settings.LoadAsync();
        await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard, settings.Revision, CancellationToken.None);
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Book, MediaCapability.Instant);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, (await service.SubmitAsync(Draft("instant", MediaAcquisitionKind.Book), CancellationToken.None)).Status);
        settings = await fixture.Settings.LoadAsync();
        await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Kinds = [MediaAcquisitionKind.Book] }, settings.Revision, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => service.SubmitAsync(Draft("rule-denied", MediaAcquisitionKind.Manga), CancellationToken.None));
    }

    [TestMethod]
    public async Task Approval_AutomaticAndInstantCannotBypassDisabledModules()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var directory = Directory.CreateTempSubdirectory("jularr-rule-modules-");
        try
        {
            var modules = new InstanceModuleStore(directory.FullName);
            var settings = await fixture.Settings.LoadAsync();
            await fixture.Settings.SaveProfileAsync(1, "Standard", null, RequestRuleValues.Standard with { Approval = RequestApprovalMode.Automatic }, settings.Revision, CancellationToken.None);
            var service = new AcquisitionRequestService(fixture.Store, [], AcquisitionAccessFixture.Account("owner", AccountRole.Owner), new MediaCapabilityService(fixture.Capabilities),
                fixture.Settings, fixture.Events, NullLogger<AcquisitionRequestService>.Instance, modules);
            await modules.SetAsync(InstanceModule.Book, false);
            await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => service.SubmitAsync(Draft("disabled-book", MediaAcquisitionKind.Book), CancellationToken.None));
            await modules.SetAsync(InstanceModule.Book, true);
            await modules.SetAsync(InstanceModule.Acquisition, false);
            await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(() => service.SubmitAsync(Draft("disabled-acquisition", MediaAcquisitionKind.Book), CancellationToken.None));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static AcquisitionRequestDraft Draft(string id, MediaAcquisitionKind kind) => new(kind, "test", id, id, null, null);
}
