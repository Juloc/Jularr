using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeMonitoringTests
{
    private static readonly AnimeQualityProfile Profile = AnimeQualityProfiles.CreateDefaultAnime1080p();

    [TestMethod]
    public void MissingAiredEpisodeBecomesWantedButFutureEpisodeDoesNot()
    {
        var now = DateTimeOffset.Parse("2026-09-25T18:00:00Z");
        var inventory = new[]
        {
            new AnimeEpisodeInventory(new AnimeEpisodeKey("anime", 1, 1), now.AddMinutes(-5), false, null),
            new AnimeEpisodeInventory(new AnimeEpisodeKey("anime", 1, 2), now.AddHours(2), false, null)
        };

        var wanted = AnimeMonitoringEngine.GetWanted(_ => true, inventory, Profile, now);

        Assert.AreEqual(1, wanted.Count);
        Assert.AreEqual(1, wanted[0].Key.EpisodeNumber);
        Assert.AreEqual(AnimeWantedReason.Missing, wanted[0].Reason);
    }

    [TestMethod]
    public void FileBelowCutoffAppearsAsCutoffUnmet()
    {
        var now = DateTimeOffset.UtcNow;
        var current = Score("Anime - S01E01 WEB-DL 720p AVC AAC[JA]", 500_000_000);
        var inventory = new[]
        {
            new AnimeEpisodeInventory(new AnimeEpisodeKey("anime", 1, 1), now.AddDays(-1), true, current)
        };

        var wanted = AnimeMonitoringEngine.GetWanted(_ => true, inventory, Profile, now);

        Assert.AreEqual(1, wanted.Count);
        Assert.AreEqual(AnimeWantedReason.CutoffUnmet, wanted[0].Reason);
    }

    [TestMethod]
    public void SearchPlanningRespectsSearchOnAddAndRetryBackoff()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new AnimeEpisodeKey("anime", 1, 1);
        var settings = DefaultSettings() with { SearchOnAdd = false };
        var state = State(settings);
        var wanted = new[] { new AnimeWantedEpisode(key, AnimeWantedReason.Missing, now) };

        Assert.AreEqual(
            0,
            AnimeMonitoringEngine.PlanSearches(state, wanted, AnimeSearchTrigger.SearchOnAdd, now).Count);

        var failed = AnimeMonitoringEngine.MarkFailed(state, key, null, now);
        Assert.AreEqual(
            0,
            AnimeMonitoringEngine.PlanSearches(failed, wanted, AnimeSearchTrigger.PeriodicMissing, now.AddMinutes(1)).Count);

        Assert.AreEqual(
            1,
            AnimeMonitoringEngine.PlanSearches(failed, wanted, AnimeSearchTrigger.PeriodicMissing, now.AddMinutes(6)).Count);
    }

    [TestMethod]
    public void FailureBackoffIsBoundedAndExponential()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new AnimeEpisodeKey("anime", 1, 1);
        var state = State(DefaultSettings());

        for (var i = 0; i < 10; i++)
        {
            state = AnimeMonitoringEngine.MarkFailed(state, key, null, now);
            now = state.Attempts[key.ToString()].NextRetryAtUtc!.Value;
        }

        var last = state.Attempts[key.ToString()];
        Assert.IsTrue(last.NextRetryAtUtc!.Value - last.LastAttemptAtUtc!.Value <= TimeSpan.FromHours(6));
        Assert.AreEqual(10, last.FailureCount);
    }

    [TestMethod]
    public void AnOutageDelaysTheNextSearchWithoutCountingAsAFailureOrRaisingTheBackoff()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new AnimeEpisodeKey("anime", 1, 1);
        var wanted = new[] { new AnimeWantedEpisode(key, AnimeWantedReason.Missing, now) };
        var state = AnimeMonitoringEngine.MarkFailed(AnimeMonitoringEngine.MarkFailed(State(DefaultSettings()), key, null, now), key, null, now);
        var failures = state.Attempts[key.ToString()].FailureCount;

        var outage = AnimeMonitoringEngine.MarkUnavailable(state, key, now, TimeSpan.FromMinutes(30), "No indexer could be searched.");

        var attempt = outage.Attempts[key.ToString()];
        Assert.AreEqual(failures, attempt.FailureCount, "The failure count, which drives the back-off, is untouched.");
        Assert.AreNotEqual(AcquisitionAttemptStatus.Failed, attempt.Status);
        Assert.AreEqual(now + TimeSpan.FromMinutes(30), attempt.NextRetryAtUtc);
        Assert.AreEqual(0, AnimeMonitoringEngine.PlanSearches(outage, wanted, AnimeSearchTrigger.PeriodicMissing, now.AddMinutes(29)).Count, "No search before the retry delay.");
        Assert.AreEqual(1, AnimeMonitoringEngine.PlanSearches(outage, wanted, AnimeSearchTrigger.PeriodicMissing, now.AddMinutes(31)).Count, "The episode is searched again once it has passed.");
        Assert.AreEqual("unavailable", outage.History[^1].Event);
    }

    [TestMethod]
    public void CandidateForMissingEpisodeCanAutoGrab()
    {
        var key = new AnimeEpisodeKey("anime", 1, 1);
        var wanted = new AnimeWantedEpisode(key, AnimeWantedReason.Missing, DateTimeOffset.UtcNow);
        var candidate = Score("Anime - S01E01 WEB-DL 1080p AVC AAC[JA]", 900_000_000);

        var decision = AnimeMonitoringEngine.EvaluateCandidate(
            Profile,
            wanted,
            candidate,
            null,
            State(DefaultSettings()));

        Assert.IsTrue(decision.Grab);
    }

    [TestMethod]
    public void CandidateForWrongEpisodeIsRejected()
    {
        var key = new AnimeEpisodeKey("anime", 1, 1);
        var wanted = new AnimeWantedEpisode(key, AnimeWantedReason.Missing, DateTimeOffset.UtcNow);
        var candidate = Score("Anime - S01E02 WEB-DL 1080p AVC AAC[JA]", 900_000_000);

        var decision = AnimeMonitoringEngine.EvaluateCandidate(
            Profile,
            wanted,
            candidate,
            null,
            State(DefaultSettings()));

        Assert.IsFalse(decision.Grab);
    }

    [TestMethod]
    public void AbsoluteNumberedCandidateMatchesMappedLocalEpisode()
    {
        var key = new AnimeEpisodeKey("anime", 2, 1, 13);
        var wanted = new AnimeWantedEpisode(key, AnimeWantedReason.Missing, DateTimeOffset.UtcNow);
        var candidate = Score("[Group] Anime - 13 WEB-DL 1080p AVC AAC[JA]", 900_000_000);

        var decision = AnimeMonitoringEngine.EvaluateCandidate(
            Profile,
            wanted,
            candidate,
            null,
            State(DefaultSettings()));

        Assert.IsTrue(decision.Grab);
    }

    [TestMethod]
    public void RefreshWantedPersistsBecameWantedAndHistoryWithoutDuplicates()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new AnimeEpisodeKey("anime", 1, 1);
        var inventory = new[]
        {
            new AnimeEpisodeInventory(key, now.AddHours(-1), false, null)
        };

        var first = AnimeMonitoringEngine.RefreshWanted(AnimeMonitoringState.Empty(), _ => true, inventory, Profile, now);
        var second = AnimeMonitoringEngine.RefreshWanted(first, _ => true, inventory, Profile, now.AddMinutes(10));

        Assert.AreEqual(1, second.Wanted.Count);
        Assert.AreEqual(now, second.Wanted[key.ToString()].BecameWantedAtUtc);
        Assert.AreEqual(1, second.History.Count(entry => entry.Event == "wanted"));
    }

    [TestMethod]
    public void DuplicateReleaseKeyIsNotGrabbedTwice()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new AnimeEpisodeKey("anime", 1, 1);
        var candidate = Score("Anime - S01E01 WEB-DL 1080p AVC AAC[JA]", 900_000_000);
        var state = AnimeMonitoringEngine.MarkGrabbed(
            State(DefaultSettings()),
            key,
            candidate.Candidate.Release.ReleaseKey,
            now);

        var decision = AnimeMonitoringEngine.EvaluateCandidate(
            Profile,
            new AnimeWantedEpisode(key, AnimeWantedReason.Missing, now),
            candidate,
            null,
            state);

        Assert.IsFalse(decision.Grab);
    }

    [TestMethod]
    public async Task StorePersistsSchedulerStateAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "jularr-monitoring-" + Guid.NewGuid());
        try
        {
            var store = new AnimeMonitoringStore(root);
            var now = DateTimeOffset.UtcNow;
            var key = new AnimeEpisodeKey("anime", 1, 1);
            var state = AnimeMonitoringEngine.MarkFailed(State(DefaultSettings()), key, "release", now);

            await store.SaveAsync(state);

            var reloaded = await new AnimeMonitoringStore(root).LoadAsync();

            Assert.AreEqual(1, reloaded.Attempts.Count);
            Assert.AreEqual(1, reloaded.History.Count);
            Assert.AreEqual(AnimeAcquisitionAttemptStatus.Failed, reloaded.Attempts[key.ToString()].Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AnimeReleaseScoreResult Score(string title, long size) =>
        AnimeReleaseScorer.Score(
            Profile,
            new AnimeReleaseCandidate(AnimeReleaseParser.Parse(title), size));

    private static AnimeMonitorSettings DefaultSettings() => new("anime", SearchOnAdd: true);

    private static AnimeMonitoringState State(AnimeMonitorSettings settings)
    {
        var state = AnimeMonitoringState.Empty();
        state.Anime[settings.AnimeKey] = settings;
        return state;
    }
}
