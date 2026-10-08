using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Tests;

[TestClass]
public sealed class ReleaseSelectionEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static QualityProfile Profile() => VideoQualityProfiles.CreateDefaultTv1080p();

    private static SelectionCandidate Candidate(
        string id,
        string title,
        ReleaseIdentityEvidence? identity = null,
        SelectionCoverage? coverage = null,
        int priority = 0,
        DateTimeOffset? posted = null,
        ReleaseReliability? reliability = null) =>
        new(id, ReleaseParser.Parse(title), 2_000_000_000, "Indexer", priority, posted ?? Now.AddDays(-1), identity ?? ReleaseIdentityEvidence.Strong("Matches", "ok"), coverage ?? SelectionCoverage.Single)
        {
            Reliability = reliability
        };

    private static SelectionResult Select(QualityProfile profile, params SelectionCandidate[] candidates) =>
        ReleaseSelectionEngine.Select(profile, new SelectionContext(Now, Now), candidates);

    [TestMethod]
    public void AHighScoreNeverRepairsAWrongIdentity()
    {
        var profile = Profile() with { ScoreRules = [new("Great group", ReleaseRuleField.ReleaseGroup, ReleaseRuleMatch.Equals, "GRP", 500)] };
        var wrong = Candidate("wrong", "Show.S01E02.2160p.BluRay.H264-GRP", ReleaseIdentityEvidence.Conflict("WrongEpisode", "The release is for another episode."));
        var right = Candidate("right", "Show.S01E01.1080p.WEB-DL.H264-OTHER");

        var result = Select(profile, wrong, right);

        Assert.AreEqual("right", result.Winner!.Candidate.Id);
        var rejected = result.Ranked.Single(evaluation => evaluation.Candidate.Id == "wrong");
        Assert.AreEqual(SelectionDecision.Rejected, rejected.Decision);
        Assert.IsTrue(rejected.Reasons.Any(reason => reason.Kind == SelectionReasonKind.Identity && reason.Code == "WrongEpisode"));
    }

    [TestMethod]
    public void RequireRejectAndInfoRulesAreGatesAndNotScores()
    {
        var profile = Profile() with
        {
            ScoreRules =
            [
                new("German audio", ReleaseRuleField.RawTitle, ReleaseRuleMatch.Contains, "German", 0) { Effect = ReleaseRuleEffect.Require },
                new("No hardcoded subs", ReleaseRuleField.RawTitle, ReleaseRuleMatch.Contains, "HC", 0) { Effect = ReleaseRuleEffect.Reject },
                new("Remux", ReleaseRuleField.RawTitle, ReleaseRuleMatch.Contains, "REMUX", 0) { Effect = ReleaseRuleEffect.Info },
                new("Prefer WEB", ReleaseRuleField.Source, ReleaseRuleMatch.Equals, "WebDl", 5)
            ]
        };

        var result = Select(
            profile,
            Candidate("ok", "Show.S01E01.German.1080p.WEB-DL.H264-GRP"),
            Candidate("missing", "Show.S01E01.1080p.WEB-DL.H264-GRP"),
            Candidate("hardcoded", "Show.S01E01.German.HC.1080p.WEB-DL.H264-GRP"));

        Assert.AreEqual("ok", result.Winner!.Candidate.Id);
        Assert.IsTrue(result.Ranked.Single(evaluation => evaluation.Candidate.Id == "missing").Reasons.Any(reason => reason.Detail.Contains("Missing required rule 'German audio'")));
        Assert.IsTrue(result.Ranked.Single(evaluation => evaluation.Candidate.Id == "hardcoded").Reasons.Any(reason => reason.Detail.Contains("reject rule 'No hardcoded subs'")));
        Assert.AreEqual(5, result.Winner.PreferenceScore, "Only Prefer and Avoid rules move the score.");
    }

    [TestMethod]
    public void AmbiguousIdentityWaitsForManualReviewUnlessTheProfileAllowsIt()
    {
        var ambiguous = Candidate("maybe", "Show.S01E01.1080p.WEB-DL.H264-GRP", ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", "may be another film"));

        var strict = Select(Profile(), ambiguous);
        var lenient = Select(Profile() with { AllowAmbiguousIdentity = true }, ambiguous);

        Assert.IsNull(strict.Winner);
        Assert.AreEqual(SelectionDecision.ManualReview, strict.Ranked[0].Decision);
        Assert.AreEqual(SelectionOutcome.ManualReviewOnly, strict.Outcome);
        Assert.AreEqual("maybe", lenient.Winner!.Candidate.Id);
    }

    [TestMethod]
    public void FallbackTiersOpenOnlyAfterTheirWaitAndTheResultIsTemporary()
    {
        var profile = Profile() with
        {
            AllowedQualities = ["WEB-1080p"],
            FallbackTiers = [new FallbackTier(120, ["WEB-720p"]), new FallbackTier(1440, ["HDTV-720p"])]
        };
        var hd = Candidate("hd", "Show.S01E01.1080p.WEB-DL.H264-GRP");
        var lower = Candidate("lower", "Show.S01E01.720p.WEB-DL.H264-GRP");

        var early = ReleaseSelectionEngine.Select(profile, new SelectionContext(Now, Now.AddMinutes(-30)), [lower]);
        var later = ReleaseSelectionEngine.Select(profile, new SelectionContext(Now, Now.AddMinutes(-180)), [lower]);
        var both = ReleaseSelectionEngine.Select(profile, new SelectionContext(Now, Now.AddMinutes(-180)), [lower, hd]);

        Assert.IsNull(early.Winner);
        Assert.IsTrue(early.Ranked[0].Reasons.Any(reason => reason.Code == "WaitingForFallbackTier" && reason.Detail.Contains("120 minutes")));
        Assert.AreEqual(SelectionDecision.Temporary, later.Winner!.Decision);
        Assert.AreEqual(1, later.Winner.FallbackTier);
        Assert.AreEqual("hd", both.Winner!.Candidate.Id, "A candidate that fits the profile itself always beats a fallback.");
    }

    [TestMethod]
    public void APackWinsWhenMostOfASeasonIsWantedAndASingleEpisodeWinsWhenOnlyOneIs()
    {
        var pack = Candidate("pack", "Show.S01.1080p.WEB-DL.H264-GRP", coverage: new SelectionCoverage(10, 10, 0));
        var single = Candidate("single", "Show.S01E01.1080p.WEB-DL.H264-GRP", coverage: new SelectionCoverage(1, 10, 0));
        Assert.AreEqual("pack", Select(Profile(), single, pack).Winner!.Candidate.Id);

        var packForOne = Candidate("pack", "Show.S01.1080p.WEB-DL.H264-GRP", coverage: new SelectionCoverage(1, 1, 9));
        var singleForOne = Candidate("single", "Show.S01E01.1080p.WEB-DL.H264-GRP", coverage: new SelectionCoverage(1, 1, 0));
        Assert.AreEqual("single", Select(Profile(), packForOne, singleForOne).Winner!.Candidate.Id);
    }

    [TestMethod]
    public void TheWinnerIsIndependentOfTheOrderTheCandidatesArriveIn()
    {
        var a = Candidate("a", "Show.S01E01.1080p.WEB-DL.H264-GRP", priority: 2, posted: Now.AddDays(-2));
        var b = Candidate("b", "Show.S01E01.1080p.WEB-DL.H264-GRP", priority: 1, posted: Now.AddDays(-3));
        var c = Candidate("c", "Show.S01E01.1080p.WEB-DL.H264-GRP", priority: 1, posted: Now.AddDays(-3));

        var forward = Select(Profile(), a, b, c);
        var backward = Select(Profile(), c, b, a);

        CollectionAssert.AreEqual(forward.Ranked.Select(evaluation => evaluation.Candidate.Id).ToArray(), backward.Ranked.Select(evaluation => evaluation.Candidate.Id).ToArray());
        Assert.AreEqual("b", forward.Winner!.Candidate.Id, "The indexer priority decides before the stable identifier.");
        Assert.AreEqual("Equal in every respect; the stable order decides.", forward.WinnerReason);
        Assert.AreEqual("Its indexer has the higher priority.", Select(Profile(), a, b).WinnerReason);
    }

    [TestMethod]
    public void ReliabilityIsABoundedLateTiebreakThatNeverBeatsQualityOrScore()
    {
        var proven = new ReleaseReliability(20, 20);
        var unproven = new ReleaseReliability(2, 0);
        Assert.AreEqual(ReleaseReliability.MaximumPoints, proven.Points);
        Assert.AreEqual(0, unproven.Points, "Too few samples contribute nothing.");
        Assert.AreEqual(-ReleaseReliability.MaximumPoints, new ReleaseReliability(20, 0).Points);

        var better = Candidate("better", "Show.S01E01.1080p.WEB-DL.H264-GRP", reliability: new ReleaseReliability(20, 0));
        var worse = Candidate("worse", "Show.S01E01.720p.WEB-DL.H264-GRP", reliability: proven);
        Assert.AreEqual("better", Select(Profile(), better, worse).Winner!.Candidate.Id, "A reliable lower quality does not win.");

        var tieA = Candidate("a", "Show.S01E01.1080p.WEB-DL.H264-GRP", reliability: new ReleaseReliability(20, 0));
        var tieB = Candidate("b", "Show.S01E01.1080p.WEB-DL.H264-GRP", reliability: proven);
        Assert.AreEqual("b", Select(Profile(), tieA, tieB).Winner!.Candidate.Id);
    }

    [TestMethod]
    public void TheOutcomeSeparatesMissingIdentityFromProfileRejection()
    {
        var wrong = Candidate("wrong", "Show.S01E02.1080p.WEB-DL.H264-GRP", ReleaseIdentityEvidence.Conflict("WrongEpisode", "other episode"));
        var tooSmallProfile = Profile() with { MinimumSizeBytes = 10_000_000_000 };

        Assert.AreEqual(SelectionOutcome.NoCandidates, Select(Profile()).Outcome);
        Assert.AreEqual(SelectionOutcome.IdentityInvalid, Select(Profile(), wrong).Outcome);
        Assert.AreEqual(SelectionOutcome.ProfileRejected, Select(tooSmallProfile, Candidate("small", "Show.S01E01.1080p.WEB-DL.H264-GRP")).Outcome);
        Assert.AreEqual(SelectionOutcome.Usable, Select(Profile(), Candidate("ok", "Show.S01E01.1080p.WEB-DL.H264-GRP")).Outcome);
    }

    [TestMethod]
    public void SafetyRejectionsAreFirstClassAndNotNegativePreferences()
    {
        var unsafeCandidate = Candidate("unsafe", "Show.S01E01.1080p.WEB-DL.H264-GRP") with { SafetyRejection = "The indexer returned no download link." };

        var result = Select(Profile(), unsafeCandidate);

        Assert.AreEqual(SelectionDecision.Rejected, result.Ranked[0].Decision);
        Assert.AreEqual(SelectionReasonKind.Safety, result.Ranked[0].Reasons[0].Kind);
        Assert.IsNull(result.Ranked[0].Score, "A safety rejection is decided before any score exists.");
    }

    [TestMethod]
    public void AProfilesFallbackTierIsTheSharedEnginesTimedLadder()
    {
        var profile = AnimeQualityProfiles.CreateDefaultAnime1080p();
        var ladder = profile with
        {
            AllowedQualities = ["BLURAY-1080p"],
            FallbackTiers = [new FallbackTier(60, [.. profile.AllowedQualities.Except(["BLURAY-1080p"], StringComparer.OrdinalIgnoreCase)])]
        };
        var web = Candidate("web", "Show.S01E01.1080p.WEB-DL.H264-GRP");
        var bluRay = Candidate("bluray", "Show.S01E01.1080p.BluRay.H264-GRP");

        CollectionAssert.AreEqual(new[] { "BLURAY-1080p" }, ladder.AllowedQualities, "Only the qualities that reach the cutoff are allowed at once.");
        Assert.AreEqual(60, Assert.ContainsSingle(ladder.FallbackTiers).AfterMinutes);
        Assert.AreEqual(SelectionOutcome.ProfileRejected, ReleaseSelectionEngine.Select(ladder, new SelectionContext(Now, Now), [web]).Outcome, "A release below the cutoff waits.");
        Assert.AreEqual("bluray", ReleaseSelectionEngine.Select(ladder, new SelectionContext(Now, Now), [web, bluRay]).Winner!.Candidate.Id, "A release that meets the cutoff bypasses the wait.");
        var afterWait = ReleaseSelectionEngine.Select(ladder, new SelectionContext(Now, Now.AddMinutes(-61)), [web]);
        Assert.AreEqual(SelectionDecision.Temporary, afterWait.Winner!.Decision, "After the wait the lower quality is taken, and the target stays wanted for an upgrade.");
    }

    [TestMethod]
    public void AFileWhoseQualityCannotBeReadIsReplacedByAnyKnownQualityOnlyWhereTheMediaTypeAsksForIt()
    {
        var profile = Profile() with { UpgradeCutoffQuality = "BLURAY-1080p" };

        Assert.IsTrue(UpgradePolicy.IsUpgrade(profile, "UNKNOWN-UNKNOWN", "WEB-720p", upgradeUnknownInstalled: true));
        Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "UNKNOWN-UNKNOWN", "WEB-720p"), "A scanned file of unknown quality is not churned.");
    }

    [TestMethod]
    public void UpgradeNeedsAMeaningfulBenefit()
    {
        var profile = Profile() with { UpgradeMinimumScoreDelta = 10, UpgradeUntilScore = 40, UpgradeCutoffQuality = "BLURAY-1080p" };
        ReleaseScoreResult Result(string quality, int rank, int score) => new(new ReleaseCandidate(ReleaseParser.Parse("Show.S01E01.1080p.WEB-DL.H264-GRP")), true, score, quality, rank, [], []);
        var current = Result("WEB-1080p", 1, 10);

        Assert.IsFalse(ReleaseScorer.IsUpgrade(profile, current, Result("WEB-1080p", 1, 14)), "A small score difference never churns files.");
        Assert.IsTrue(ReleaseScorer.IsUpgrade(profile, current, Result("WEB-1080p", 1, 25)));
        Assert.IsTrue(ReleaseScorer.IsUpgrade(profile, current, Result("BLURAY-1080p", 0, 10)), "A better quality tier is an upgrade.");
        Assert.IsFalse(ReleaseScorer.IsUpgrade(profile, Result("WEB-1080p", 1, 45), Result("WEB-1080p", 1, 90)), "Upgrades stop at the configured score.");
        Assert.IsFalse(ReleaseScorer.IsUpgrade(profile, current, Result("HDTV-1080p", 2, 99)), "Never a downgrade.");
        Assert.IsFalse(ReleaseScorer.IsUpgrade(Profile() with { UpgradeMinimumQualitySteps = 2, UpgradeCutoffQuality = "BLURAY-1080p" }, current, Result("BLURAY-1080p", 0, 10)), "One step is not enough when two are required.");
    }

    [TestMethod]
    public void ProfileValidationRejectsUnorderedFallbackTiersAndUnknownQualities()
    {
        var unordered = Profile() with { FallbackTiers = [new FallbackTier(600, ["WEB-720p"]), new FallbackTier(60, ["HDTV-720p"])] };
        var unknown = Profile() with { FallbackTiers = [new FallbackTier(60, ["WEB-480p"])] };

        Assert.IsTrue(ReleaseScorer.ValidateProfile(unordered).Any(error => error.Contains("wait longer")));
        Assert.IsTrue(ReleaseScorer.ValidateProfile(unknown).Any(error => error.Contains("WEB-480p")));
        Assert.AreEqual(0, ReleaseScorer.ValidateProfile(Profile()).Count);
    }

    [TestMethod]
    public void AMovieSequelIsAmbiguousAndAWrongYearIsAConflict()
    {
        var parser = SceneReleaseParser.Instance;
        ReleaseJudgement<VideoIdentityMatch> Judge(string release, int? year = 2021, IReadOnlyList<QueryProvenance>? origin = null) =>
            VideoReleaseJudge.Judge(parser, MediaAcquisitionKind.Movie, "Dune", year, null, VideoUnitScope.Empty, Release(release, origin));

        Assert.AreEqual(IdentityConfidence.Exact, Judge("Dune.2021.1080p.BluRay.x264-GRP").Evidence.Confidence);
        Assert.AreEqual(IdentityConfidence.Strong, Judge("Dune.1080p.BluRay.x264-GRP").Evidence.Confidence);
        Assert.AreEqual(IdentityConfidence.Strong, Judge("Dune.EXTENDED.1080p.BluRay.x264-GRP").Evidence.Confidence, "An edition tag is not part of the title.");
        Assert.AreEqual(IdentityConfidence.Ambiguous, Judge("Dune.Part.Two.1080p.BluRay.x264-GRP", 2021).Evidence.Confidence);
        Assert.AreEqual(VideoIdentityMatch.WrongYear, Judge("Dune.1984.1080p.BluRay.x264-GRP").Match);
        Assert.AreEqual(VideoIdentityMatch.WrongTitle, Judge("Arrival.2016.1080p.BluRay.x264-GRP").Match);

        var viaId = new[] { new QueryProvenance("Indexer", "id", "TMDB ID", "imdbid=1") };
        Assert.AreEqual(IdentityConfidence.Ambiguous, Judge("Der.Wuestenplanet.2021.1080p.BluRay.x264-GRP", origin: viaId).Evidence.Confidence, "A different title found by the provider id needs a person.");
        Assert.AreEqual(IdentityConfidence.Exact, Judge("Dune.2021.1080p.BluRay.x264-GRP", origin: viaId).Evidence.Confidence);
    }

    [TestMethod]
    public void ASeasonPackCoversTheWantedUnitsOfItsSeasonAndChargesTheOthers()
    {
        var episodes = Enumerable.Range(1, 10).Select(number => new VideoUnit(Guid.NewGuid(), null, 1, number, Now.DateTime.AddDays(-30), HasFile: number > 8)).ToArray();
        var scope = new VideoUnitScope(episodes, [.. episodes.Where(unit => !unit.HasFile)]);
        var parser = SceneReleaseParser.Instance;

        var pack = VideoReleaseJudge.Judge(parser, MediaAcquisitionKind.Tv, "Show", null, episodes[0], scope, Release("Show.S01.1080p.WEB-DL.H264-GRP"));
        var wrongSeason = VideoReleaseJudge.Judge(parser, MediaAcquisitionKind.Tv, "Show", null, episodes[0], scope, Release("Show.S02E01.1080p.WEB-DL.H264-GRP"));
        var multi = VideoReleaseJudge.Judge(parser, MediaAcquisitionKind.Tv, "Show", null, episodes[0], scope, Release("Show.S01E01-E03.1080p.WEB-DL.H264-GRP"));

        Assert.AreEqual(VideoIdentityMatch.ContainsTarget, pack.Match);
        Assert.AreEqual(8, pack.Coverage.WantedCovered);
        Assert.AreEqual(2, pack.Coverage.Unwanted, "The two episodes the library already has are duplicate cost.");
        Assert.AreEqual(IdentityConfidence.Conflict, wrongSeason.Evidence.Confidence);
        Assert.AreEqual(3, multi.Coverage.WantedCovered);
    }

    private static ProwlarrReleaseCandidate Release(string title, IReadOnlyList<QueryProvenance>? provenance = null) =>
        new(title, "Indexer", null, "usenet", 2_000_000_000, null, null, Now, 1, 24, Guid.NewGuid().ToString("N"), null, ReleaseParser.Parse(title), [], new Uri("http://indexer.example/nzb/1"), null)
        {
            Provenance = provenance ?? []
        };
}
