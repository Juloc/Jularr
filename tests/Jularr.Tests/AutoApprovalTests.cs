using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>#597: rule- and quota-based auto-approval on top of the Request capability.</summary>
[TestClass]
public sealed class AutoApprovalTests
{
    private static readonly IReadOnlyDictionary<string, int> NoUsage = new Dictionary<string, int>();

    [TestMethod]
    public void NoRuleMeansTheOwnerDecides()
    {
        var decision = AutoApprovalEvaluator.Evaluate([], MediaAcquisitionKind.Book, "alice", NoUsage);

        Assert.IsFalse(decision.AutoApprove);
        Assert.IsNull(decision.Rule);
    }

    [TestMethod]
    public void RulesMatchOnMediaTypeAndRequesterAndAnEmptyListMeansEveryone()
    {
        var mangaOnly = Rule("manga", kinds: [MediaAcquisitionKind.Manga]);
        var aliceOnly = Rule("alice", profileIds: ["alice"]);
        var everything = Rule("all");

        Assert.IsTrue(mangaOnly.Matches(MediaAcquisitionKind.Manga, "bob"));
        Assert.IsFalse(mangaOnly.Matches(MediaAcquisitionKind.Book, "bob"));
        Assert.IsTrue(aliceOnly.Matches(MediaAcquisitionKind.Anime, "alice"));
        Assert.IsFalse(aliceOnly.Matches(MediaAcquisitionKind.Anime, "bob"));
        Assert.IsTrue(everything.Matches(MediaAcquisitionKind.LightNovel, "anyone"));
        Assert.IsFalse((everything with { Enabled = false }).Matches(MediaAcquisitionKind.LightNovel, "anyone"));

        var decision = AutoApprovalEvaluator.Evaluate([mangaOnly, aliceOnly], MediaAcquisitionKind.Manga, "bob", NoUsage);
        Assert.AreEqual(mangaOnly.Id, decision.Rule?.Id);
        Assert.IsFalse(AutoApprovalEvaluator.Evaluate([mangaOnly, aliceOnly], MediaAcquisitionKind.Book, "bob", NoUsage).AutoApprove);
    }

    [TestMethod]
    public void AQuotaApprovesUntilItIsUsedUpThenTheNextRuleOrTheOwnerTakesOver()
    {
        var limited = Rule("limited", quota: new AutoApprovalQuota(2, 7));
        var trusted = Rule("trusted", profileIds: ["carol"]);
        var rules = new[] { limited, trusted };

        Assert.AreEqual(limited.Id, Evaluate(rules, "bob", used: 0).Rule?.Id);
        Assert.AreEqual(limited.Id, Evaluate(rules, "bob", used: 1).Rule?.Id);
        Assert.IsFalse(Evaluate(rules, "bob", used: 2).AutoApprove, "The quota is used up and no other rule matches bob.");
        Assert.AreEqual(trusted.Id, Evaluate(rules, "carol", used: 2).Rule?.Id, "A later rule still applies when an earlier quota is used up.");

        AutoApprovalDecision Evaluate(AutoApprovalRule[] all, string profile, int used) =>
            AutoApprovalEvaluator.Evaluate(all, MediaAcquisitionKind.Book, profile, new Dictionary<string, int> { [limited.Id] = used });
    }

    [TestMethod]
    public void OnlyMatchingRulesWithAQuotaNeedTheirUsageCounted()
    {
        var unlimited = Rule("unlimited");
        var limitedManga = Rule("manga", kinds: [MediaAcquisitionKind.Manga], quota: new AutoApprovalQuota(1, 1));
        var limitedBook = Rule("book", kinds: [MediaAcquisitionKind.Book], quota: new AutoApprovalQuota(1, 1));
        var off = Rule("off", quota: new AutoApprovalQuota(1, 1)) with { Enabled = false };

        var needed = AutoApprovalEvaluator.QuotaRulesFor([unlimited, limitedManga, limitedBook, off], MediaAcquisitionKind.Book, "alice");

        CollectionAssert.AreEqual(new[] { limitedBook.Id }, needed.Select(rule => rule.Id).ToArray());
    }

    [TestMethod]
    public void RuleCreationValidatesNameAndQuota()
    {
        var rule = AutoApprovalRule.Create("  Trusted  ", [MediaAcquisitionKind.Book, MediaAcquisitionKind.Anime, MediaAcquisitionKind.Book], ["a", " ", "a"], new AutoApprovalQuota(5, 7));

        Assert.AreEqual("Trusted", rule.Name);
        CollectionAssert.AreEqual(new[] { MediaAcquisitionKind.Anime, MediaAcquisitionKind.Book }, rule.Kinds.ToArray());
        CollectionAssert.AreEqual(new[] { "a" }, rule.ProfileIds.ToArray());
        Assert.IsTrue(rule.Enabled);

        Assert.ThrowsExactly<ArgumentException>(() => AutoApprovalRule.Create(" ", [], [], null));
        Assert.ThrowsExactly<ArgumentException>(() => AutoApprovalRule.Create("x", [], [], new AutoApprovalQuota(0, 7)));
        Assert.ThrowsExactly<ArgumentException>(() => AutoApprovalRule.Create("x", [], [], new AutoApprovalQuota(5, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => AutoApprovalRule.Create("x", [], [], new AutoApprovalQuota(5, AutoApprovalQuota.PeriodDaysLimit + 1)));
    }

    [TestMethod]
    public async Task SettingsPersistRulesAndTheProfilesRequestersMayPick()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jularr-request-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new AcquisitionRequestSettingsStore(directory);
            Assert.AreEqual(0, (await store.LoadAsync()).AutoApprovalRules.Count, "No file yet: no rules.");

            var first = Rule("first", kinds: [MediaAcquisitionKind.Manga], quota: new AutoApprovalQuota(3, 14));
            var second = Rule("second", profileIds: ["alice"]);
            await store.AddRuleAsync(first);
            await store.AddRuleAsync(second);
            await store.SetRuleEnabledAsync(second.Id, false);
            await store.SetRequesterQualityProfilesAsync(["anime-1080p", " ", "anime-1080p", "anime-720p"]);

            var reloaded = await new AcquisitionRequestSettingsStore(directory).LoadAsync();
            CollectionAssert.AreEqual(new[] { first.Id, second.Id }, reloaded.AutoApprovalRules.Select(rule => rule.Id).ToArray(), "Rules keep the order they were added in.");
            Assert.AreEqual(new AutoApprovalQuota(3, 14), reloaded.AutoApprovalRules[0].Quota);
            CollectionAssert.AreEqual(new[] { MediaAcquisitionKind.Manga }, reloaded.AutoApprovalRules[0].Kinds.ToArray());
            Assert.IsFalse(reloaded.AutoApprovalRules[1].Enabled);
            CollectionAssert.AreEqual(new[] { "alice" }, reloaded.AutoApprovalRules[1].ProfileIds.ToArray());
            CollectionAssert.AreEqual(new[] { "anime-1080p", "anime-720p" }, reloaded.RequesterQualityProfileIds.ToArray());

            await store.RemoveRuleAsync(first.Id);
            Assert.AreEqual(1, (await new AcquisitionRequestSettingsStore(directory).LoadAsync()).AutoApprovalRules.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task AMatchingRuleApprovesARequestAtOnceUntilTheRequestersQuotaIsUsed()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var rule = Rule("weekly", quota: new AutoApprovalQuota(2, 7));
        await fixture.Settings.AddRuleAsync(rule);
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var alice = fixture.Service("alice", AccountRole.User, executor);
        var bob = fixture.Service("bob", AccountRole.User, executor);

        var one = await alice.SubmitAsync(Draft("one"), CancellationToken.None);
        var two = await alice.SubmitAsync(Draft("two"), CancellationToken.None);
        var three = await alice.SubmitAsync(Draft("three"), CancellationToken.None);
        var bobsFirst = await bob.SubmitAsync(Draft("four"), CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, one.Status);
        Assert.AreEqual(AcquisitionAutoApproval.DecidedBy(rule.Id), one.DecidedByProfileId);
        Assert.IsTrue(one.WasAutoApproved);
        Assert.IsNotNull(one.DecidedAt);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, two.Status);
        Assert.AreEqual(AcquisitionRequestStatus.Pending, three.Status, "Alice's quota is used up; the owner decides.");
        Assert.IsFalse(three.WasAutoApproved);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, bobsFirst.Status, "The quota is per requester.");
        Assert.AreEqual(3, executor.Runs);
    }

    [TestMethod]
    public async Task ARequestOutsideEveryRuleWaitsForTheOwnerAndDisabledRulesDoNothing()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        await fixture.Settings.AddRuleAsync(Rule("manga only", kinds: [MediaAcquisitionKind.Manga]));
        var off = Rule("everything, but off") with { Enabled = false };
        await fixture.Settings.AddRuleAsync(off);
        var executor = new RecordingExecutor(MediaAcquisitionKind.Book);
        var alice = fixture.Service("alice", AccountRole.User, executor);

        var book = await alice.SubmitAsync(Draft("dune"), CancellationToken.None);

        Assert.AreEqual(AcquisitionRequestStatus.Pending, book.Status);
        Assert.IsNull(book.DecidedByProfileId);
        Assert.AreEqual(0, executor.Runs);

        await fixture.Settings.SetRuleEnabledAsync(off.Id, true);
        var another = await alice.SubmitAsync(Draft("emma"), CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, another.Status, "Switching the rule on takes effect for the next request.");
    }

    [TestMethod]
    public async Task RulesNeverWidenWhatTheCapabilityAllows()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        await fixture.Settings.AddRuleAsync(Rule("everything"));
        await fixture.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Book, MediaCapability.Browse);
        var alice = fixture.Service("alice", AccountRole.User, new RecordingExecutor(MediaAcquisitionKind.Book));

        await Assert.ThrowsExactlyAsync<AcquisitionAccessDeniedException>(
            () => alice.SubmitAsync(Draft("dune"), CancellationToken.None));
    }

    [TestMethod]
    public async Task QuotaOnlyCountsRequestsInsideItsPeriod()
    {
        await using var fixture = await AcquisitionAccessFixture.CreateAsync();
        var store = fixture.Store;
        var rule = Rule("daily", quota: new AutoApprovalQuota(1, 1));
        var marker = AcquisitionAutoApproval.DecidedBy(rule.Id);
        await store.CreateAsync(Draft("mine"), "alice", AcquisitionRequestStatus.Completed, marker, CancellationToken.None);
        await store.CreateAsync(Draft("other"), "bob", AcquisitionRequestStatus.Completed, marker, CancellationToken.None);
        await store.CreateAsync(Draft("manual"), "alice", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None);

        var current = await store.CountAutoApprovedAsync("alice", new Dictionary<string, DateTime> { [rule.Id] = DateTime.UtcNow.AddDays(-1), ["other-rule"] = DateTime.UtcNow.AddDays(-1) }, CancellationToken.None);
        Assert.AreEqual(1, current[rule.Id]);
        Assert.AreEqual(0, current["other-rule"]);
        var future = await store.CountAutoApprovedAsync("alice", new Dictionary<string, DateTime> { [rule.Id] = DateTime.UtcNow.AddMinutes(1) }, CancellationToken.None);
        Assert.AreEqual(0, future[rule.Id], "Requests before the start of the period do not count.");
    }

    private static AutoApprovalRule Rule(
        string name,
        MediaAcquisitionKind[]? kinds = null,
        string[]? profileIds = null,
        AutoApprovalQuota? quota = null) =>
        AutoApprovalRule.Create(name, kinds ?? [], profileIds ?? [], quota);

    private static AcquisitionRequestDraft Draft(string id, MediaAcquisitionKind kind = MediaAcquisitionKind.Book) =>
        new(kind, "test", id, id.ToUpperInvariant(), "Author", null);
}
