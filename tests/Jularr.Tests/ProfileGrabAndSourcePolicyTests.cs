using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The rest of the one Acquisition Profile policy: a preference never repairs a gate or an identity, a source is a rule like any other
/// (penalty, preference, reject on the indexer), and a fallback-only indexer is asked only when the others found nothing.
/// </summary>
[TestClass]
public sealed class ProfileGrabAndSourcePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static QualityProfile PreferringProfile() => VideoQualityProfiles.CreateDefaultTv1080p() with
    {
        AllowedQualities = ["WEB-1080p", "WEB-720p"],
        ScoreRules = [new("Trusted group", ReleaseRuleField.ReleaseGroup, ReleaseRuleMatch.Equals, "GRP", 50)]
    };

    private static SelectionCandidate Candidate(string id, string title, string indexer = "Indexer") =>
        new(id, ReleaseParser.Parse(title), 2_000_000_000, indexer, 0, Now.AddDays(-1), ReleaseIdentityEvidence.Strong("Matches", "ok"), SelectionCoverage.Single);

    [TestMethod]
    public void AHighPreferenceNeverRepairsARejectRuleOrAWrongIdentity()
    {
        var profile = PreferringProfile() with
        {
            ScoreRules = [.. PreferringProfile().ScoreRules, new("No bad group", ReleaseRuleField.ReleaseGroup, ReleaseRuleMatch.Equals, "BAD", 0) { Effect = ReleaseRuleEffect.Reject }, new("Bad bonus", ReleaseRuleField.ReleaseGroup, ReleaseRuleMatch.Equals, "BAD", 500)]
        };
        var rejected = Candidate("bad", "Show.S01E01.720p.WEB-DL.H264-BAD");
        var wrong = new SelectionCandidate("wrong", ReleaseParser.Parse("Show.S01E02.720p.WEB-DL.H264-GRP"), 2_000_000_000, "Indexer", 0, Now.AddDays(-1), ReleaseIdentityEvidence.Conflict("WrongEpisode", "Another episode."), SelectionCoverage.Single);

        var result = ReleaseSelectionEngine.Select(profile, [rejected, wrong]);

        Assert.IsNull(result.Winner, "A reject rule and a wrong identity are not repaired by a high preference.");
    }

    [TestMethod]
    public void ASourceIsAnOrdinaryRuleOnTheIndexerNameSoItCanPenalizePreferOrReject()
    {
        var release = ReleaseParser.Parse("Show.S01E01.1080p.WEB-DL.H264-GRP");
        var profile = VideoQualityProfiles.CreateDefaultTv1080p() with
        {
            ScoreRules =
            [
                new("Slow indexer", ReleaseRuleField.Indexer, ReleaseRuleMatch.Equals, "Slow", -30),
                new("Banned indexer", ReleaseRuleField.Indexer, ReleaseRuleMatch.Equals, "Banned", 0) { Effect = ReleaseRuleEffect.Reject }
            ]
        };

        var slow = ReleaseScorer.Score(profile, new ReleaseCandidate(release, 2_000_000_000, "Slow"));
        var fast = ReleaseScorer.Score(profile, new ReleaseCandidate(release, 2_000_000_000, "Fast"));
        var banned = ReleaseScorer.Score(profile, new ReleaseCandidate(release, 2_000_000_000, "Banned"));

        Assert.AreEqual(-30, slow.Score);
        Assert.AreEqual(0, fast.Score);
        Assert.IsTrue(slow.Accepted);
        Assert.IsFalse(banned.Accepted, "A source can also be rejected outright.");
    }

    [TestMethod]
    public async Task AFallbackOnlyIndexerIsAskedOnlyWhenThePrimaryOnesFoundNothing()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            var store = new IndexerStore(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), directory);
            var primary = NewEntry("Primary", priority: 1);
            var backup = NewEntry("Backup", priority: 2);
            await store.SaveAsync(primary);
            await store.SaveAsync(backup);
            var primaryHasRelease = true;
            var searched = new List<string>();
            var indexer = new FakeIndexer((entry, _) =>
            {
                searched.Add(entry.Name);
                return entry.Name == "Primary" && !primaryHasRelease ? [] : [Release("Show.S01E01.1080p.WEB-DL.H264-GRP", entry.Name)];
            });
            var coordinator = new IndexerSearchCoordinator(new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = indexer }, store, new AcquisitionHealthStore(directory), NullLogger<IndexerSearchCoordinator>.Instance);
            var options = new SearchOptions().WithSourcePolicy(new AcquisitionSourcePolicy([], []) { FallbackOnlyEntryIds = [backup.Id] });
            var intent = new SearchIntent(MediaAcquisitionKind.Tv, "Show");

            await coordinator.SearchAsync(intent, options, CancellationToken.None);
            var askedWhileThePrimaryAnswers = searched.Distinct().ToArray();
            searched.Clear();
            primaryHasRelease = false;
            options = options with { Refresh = true };
            await coordinator.SearchAsync(intent, options, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "Primary" }, askedWhileThePrimaryAnswers, "The backup indexer costs no query while the primary one answers.");
            CollectionAssert.AreEquivalent(new[] { "Primary", "Backup" }, searched.Distinct().ToArray(), "With nothing from the primary indexers the backup is asked.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void TheFallbackOnlySourcesSurviveEditingAndAFallbackSourceOutsideTheAllowedListIsNotKept()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var form = QualityProfileEditing.ToForm(PreferringProfile());
        form.Id = "ladder";
        form.AllowedSources = [a, b];
        form.FallbackOnlySources = [b, stranger];

        var parsed = QualityProfileEditing.Parse(form);
        var again = QualityProfileEditing.ToForm(parsed.Profile!);

        Assert.AreEqual(0, parsed.Errors.Count);
        CollectionAssert.AreEqual(new[] { b }, parsed.Profile!.SourcePolicy.FallbackOnlyEntryIds);
        CollectionAssert.AreEqual(new[] { b }, again.FallbackOnlySources);
    }

    private static IndexerEntry NewEntry(string name, int priority) =>
        new(Guid.NewGuid(), name, IndexerType.Newznab, Enabled: true, priority, new IndexerSettings("https://indexer.example", [5030], [], 100), "indexer-key");

    private static AcquisitionCandidate Release(string title, string indexer) =>
        new(title, indexer, null, "usenet", 100_000_000, null, null, Now, 1, 24, title, null, null!, [], new Uri("http://indexer.example/nzb/" + Uri.EscapeDataString(title + indexer)), null);

    private sealed class FakeIndexer(Func<IndexerEntry, IndexerSearchQuery, IReadOnlyList<AcquisitionCandidate>> search) : IIndexer
    {
        public IndexerType Type => IndexerType.Newznab;

        public Task<IndexerConnectionTestResult> TestAsync(IndexerEntry entry, CancellationToken cancellationToken) => Task.FromResult(new IndexerConnectionTestResult(true));

        public Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(IndexerEntry entry, IndexerSearchQuery query, CancellationToken cancellationToken) => Task.FromResult(search(entry, query));
    }
}
