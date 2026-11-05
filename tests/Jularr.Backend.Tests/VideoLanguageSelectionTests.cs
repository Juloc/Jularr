using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;

namespace Jularr.Backend.Tests
{
    // The language chain of a film, series or anime is judged by the audio of the release: untagged audio is the original language of the work, a lower step always wins, and
    // at the original step subtitles in the chain's languages are preferred. Manga follows the same chain; the add-ons of a language add points or block.
    [TestClass]
    public sealed class VideoLanguageSelectionTests
    {
        private static QualityProfile Profile(LanguageFallbackType fallback = LanguageFallbackType.Original, string[]? order = null, int firsts = 1, string? original = "JA") =>
            new("1", "Test", ["WEBDL-1080p"], ["WEBDL-1080p"], false, null, null, null)
            {
                IsVideo = true,
                LanguageOrder = order ?? ["de", "en"],
                FirstLanguages = firsts,
                OriginalLanguage = original,
                LanguageFallback = fallback
            };

        private static SelectionCandidate Candidate(QualityProfile profile, string title, int priority = 0, string? original = "ja")
        {
            var offer = new ReleaseOffer(title, 1, "Indexer" + priority, priority, title, new Uri("https://example.org/" + Uri.EscapeDataString(title)), null, 4_000_000_000, null, ["title"]);
            var judgement = VideoReleaseJudge.Judge(AcquisitionKind.Movie, ["Example Movie"], 2020, null, offer, true, original);
            return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence, judgement.Coverage)
            {
                IndexerId = offer.ConnectionId,
                Languages = judgement.Languages ?? [],
                LanguagesAssumedOriginal = judgement.LanguagesAssumedOriginal,
                SubtitleLanguages = judgement.SubtitleLanguages,
                SubtitlesInChain = judgement.SubtitleLanguages.Any(language => language == "*" || profile.LanguageOrder.Contains(language, StringComparer.OrdinalIgnoreCase))
            };
        }

        private static ReleaseInfo Parse(string title) => ReleaseParser.TryParse(title, out var parsed) ? parsed : throw new AssertFailedException(title);

        [TestMethod]
        public void Audio_UntaggedIsTheOriginalLanguage_DlAndMultiAddItToTheNamedOnes_UnknownOriginalIsUnknown()
        {
            var untagged = VideoReleaseJudge.Languages(Parse("Example.Movie.2020.1080p.WEB-DL.H264-GRP"), "ja");
            CollectionAssert.AreEqual(new[] { "JA" }, untagged.Languages.ToArray());
            Assert.IsTrue(untagged.AssumedOriginal);

            var german = VideoReleaseJudge.Languages(Parse("Example.Movie.2020.German.1080p.WEB-DL.H264-GRP"), "ja");
            CollectionAssert.AreEqual(new[] { "DE" }, german.Languages.ToArray());
            Assert.IsFalse(german.AssumedOriginal);

            var dual = VideoReleaseJudge.Languages(Parse("Example.Movie.2020.German.DL.1080p.WEB-DL.H264-GRP"), "ja");
            CollectionAssert.AreEqual(new[] { "DE", "JA" }, dual.Languages.ToArray());

            // MULTi.SUBS is subtitles, not a second audio track.
            var subs = VideoReleaseJudge.Languages(Parse("Example.Movie.2020.German.MULTi.SUBS.1080p.WEB-DL.H264-GRP"), "ja");
            CollectionAssert.AreEqual(new[] { "DE" }, subs.Languages.ToArray());

            var unknown = VideoReleaseJudge.Languages(Parse("Example.Movie.2020.1080p.WEB-DL.H264-GRP"), null);
            Assert.AreEqual(0, unknown.Languages.Count);
        }

        [TestMethod]
        [DataRow("Example.Movie.2020.GerSub.1080p.WEB-DL.H264-GRP", "DE")]
        [DataRow("Example.Movie.2020.German.Subbed.1080p.WEB-DL.H264-GRP", "DE")]
        [DataRow("Example.Movie.2020.ENGSUB.1080p.WEB-DL.H264-GRP", "EN")]
        [DataRow("Example.Movie.2020.VOSTFR.1080p.WEB-DL.H264-GRP", "FR")]
        [DataRow("Example.Movie.2020.MULTi.SUBS.1080p.WEB-DL.H264-GRP", "*")]
        public void Subtitles_AreReadFromTheSceneTags(string title, string expected)
        {
            CollectionAssert.Contains(VideoReleaseJudge.SubtitleLanguages(Parse(title)).ToArray(), expected);
        }

        [TestMethod]
        public void Subtitles_AReleaseWithoutThemHasNone()
        {
            Assert.AreEqual(0, VideoReleaseJudge.SubtitleLanguages(Parse("Example.Movie.2020.German.DL.1080p.WEB-DL.H264-GRP")).Count);
        }

        [TestMethod]
        public void Chain_FirstBeforeSecondBeforeOriginal_WhateverTheIndexerPriorityAndOrder()
        {
            var profile = Profile();
            var original = Candidate(profile, "Example.Movie.2020.1080p.WEB-DL.H264-AAA", priority: 0);
            var second = Candidate(profile, "Example.Movie.2020.English.1080p.WEB-DL.H264-BBB", priority: 5);
            var first = Candidate(profile, "Example.Movie.2020.German.1080p.WEB-DL.H264-CCC", priority: 9);

            var result = ReleaseSelectionEngine.Select(profile, [original, second, first]);
            CollectionAssert.AreEqual(new[] { first.Id, second.Id, original.Id }, result.Ranked.Select(evaluation => evaluation.Candidate.Id).ToArray());
            Assert.AreEqual(first.Id, result.Winner!.Candidate.Id);
            Assert.AreEqual("PreferredLanguage", result.WinnerReason!.Code);
        }

        [TestMethod]
        public void Chain_AnotherKnownLanguage_IsNeverTakenAutomatically_AndSaysWhy()
        {
            var profile = Profile();
            var french = Candidate(profile, "Example.Movie.2020.French.1080p.WEB-DL.H264-AAA");

            var result = ReleaseSelectionEngine.Select(profile, [french]);
            Assert.IsNull(result.Winner);
            Assert.AreEqual(SelectionDecision.ManualReview, result.Ranked[0].Decision);
            Assert.AreEqual("LanguageNotAllowed", result.Ranked[0].Reasons[^1].Code);
            Assert.AreEqual("FR", result.Ranked[0].Reasons[^1].Value);
        }

        [TestMethod]
        public void Chain_StrictWithoutTheOriginal_RefusesAnUntaggedRelease()
        {
            var profile = Profile(LanguageFallbackType.None);
            var untagged = Candidate(profile, "Example.Movie.2020.1080p.WEB-DL.H264-AAA");
            var german = Candidate(profile, "Example.Movie.2020.German.1080p.WEB-DL.H264-BBB", priority: 3);

            var result = ReleaseSelectionEngine.Select(profile, [untagged, german]);

            Assert.AreEqual(german.Id, result.Winner!.Candidate.Id);
            Assert.AreEqual(SelectionDecision.ManualReview, result.Ranked.Single(evaluation => evaluation.Candidate.Id == untagged.Id).Decision);
        }

        [TestMethod]
        public void Chain_UnknownOriginalLanguage_MakesAnUntaggedReleaseUnknown_ATakenLastResortOnlyWhenNothingElseFits()
        {
            var profile = Profile(original: null);
            var untagged = Candidate(profile, "Example.Movie.2020.1080p.WEB-DL.H264-AAA", original: null);
            var french = Candidate(profile, "Example.Movie.2020.French.1080p.WEB-DL.H264-BBB", original: null);

            var result = ReleaseSelectionEngine.Select(profile, [untagged, french]);
            Assert.AreEqual(untagged.Id, result.Winner!.Candidate.Id);
            Assert.AreEqual("LanguageUnknownLastResort", result.WinnerReason!.Code);

            var german = Candidate(profile, "Example.Movie.2020.German.1080p.WEB-DL.H264-CCC", original: null);
            var better = ReleaseSelectionEngine.Select(profile, [untagged, german]);
            Assert.AreEqual(german.Id, better.Winner!.Candidate.Id);
            Assert.AreEqual(SelectionDecision.ManualReview, better.Ranked.Single(evaluation => evaluation.Candidate.Id == untagged.Id).Decision);
        }

        [TestMethod]
        public void OriginalStep_PrefersSubtitlesInTheChainsLanguages_BeforeIndexerPriority()
        {
            var profile = Profile();
            var plain = Candidate(profile, "Example.Movie.2020.1080p.WEB-DL.H264-AAA", priority: 0);
            var subbed = Candidate(profile, "Example.Movie.2020.GerSub.1080p.WEB-DL.H264-BBB", priority: 7);
            var otherSubs = Candidate(profile, "Example.Movie.2020.FreSub.1080p.WEB-DL.H264-CCC", priority: 1);

            var result = ReleaseSelectionEngine.Select(profile, [plain, otherSubs, subbed]);

            Assert.AreEqual(subbed.Id, result.Ranked[0].Candidate.Id);
            Assert.IsTrue(result.Ranked[0].Candidate.SubtitlesInChain);
            Assert.IsFalse(otherSubs.SubtitlesInChain);
            // A dubbed release of the first language still beats the original with subtitles.
            var german = Candidate(profile, "Example.Movie.2020.German.1080p.WEB-DL.H264-DDD", priority: 9);
            Assert.AreEqual(german.Id, ReleaseSelectionEngine.Select(profile, [subbed, german]).Winner!.Candidate.Id);
        }

        [TestMethod]
        public void Manga_ReleaseNamesWithoutALanguage_AreUnknown_NotEnglish()
        {
            var offer = new ReleaseOffer("Example Manga v03 (Digital)", 1, "Nyaa", 0, "g", new Uri("https://example.org/m"), null, 100_000_000, null, ["title"]);
            var judgement = MangaReleaseJudge.Judge(["Example Manga"], null, offer);
            Assert.AreEqual(0, (judgement.Languages ?? []).Count);

            var tagged = new ReleaseOffer("Example Manga v03 (Digital) (German)", 1, "Nyaa", 0, "g2", new Uri("https://example.org/m2"), null, 100_000_000, null, ["title"]);
            CollectionAssert.Contains((judgement.Languages ?? []).Concat(MangaReleaseJudge.Judge(["Example Manga"], null, tagged).Languages ?? []).ToArray(), "DE");
        }

        [TestMethod]
        public void Manga_FollowsTheSameChain_AnUntaggedVolumeIsOnlyALastResort()
        {
            var profile = new QualityProfile("1", "Manga", ["COMIC"], ["COMIC"], false, null, null, null)
            {
                IsManga = true,
                AllowAmbiguousIdentity = true,
                LanguageOrder = ["de"],
                FirstLanguages = 1,
                OriginalLanguage = "JA",
                LanguageFallback = LanguageFallbackType.Original
            };
            SelectionCandidate Volume(string title, int priority)
            {
                var offer = new ReleaseOffer(title, 1, "Nyaa" + priority, priority, title, new Uri("https://example.org/" + Uri.EscapeDataString(title)), null, 100_000_000, null, ["title"]);
                var judgement = MangaReleaseJudge.Judge(["Example Manga"], null, offer);
                return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence, judgement.Coverage)
                {
                    Languages = judgement.Languages ?? []
                };
            }

            var unknown = Volume("Example Manga v03 (Digital)", 0);
            var german = Volume("Example Manga v03 (German) (Digital)", 5);

            var both = ReleaseSelectionEngine.Select(profile, [unknown, german]);
            Assert.AreEqual(german.Id, both.Winner!.Candidate.Id);
            Assert.AreEqual(unknown.Id, ReleaseSelectionEngine.Select(profile, [unknown]).Winner!.Candidate.Id);
        }

        [TestMethod]
        public void AddOns_PreferredGroupAndWordAddPoints_BlockedRejects_OnlyForTheReleasesLanguage()
        {
            LanguageRule[] rules =
            [
                new("DE", ["GRP"], ["BAD"], ["Remux"], ["CAM"]),
                new("EN", ["OTHER"], [], [], [])
            ];

            var preferred = LanguageRuleLogic.Apply(rules, ["DE"], Parse("Example.Movie.2020.German.Remux.1080p.BluRay.H264-GRP"), "Example.Movie.2020.German.Remux.1080p.BluRay.H264-GRP");
            CollectionAssert.AreEqual(new[] { "LanguageGroupPreferred", "LanguageWordPreferred" }, preferred.Points.Select(point => point.Code).ToArray());
            Assert.AreEqual(LanguageRuleLogic.GroupPoints + LanguageRuleLogic.WordPoints, preferred.Points.Sum(point => point.Points));
            Assert.IsNull(preferred.Blocked);

            var blockedGroup = LanguageRuleLogic.Apply(rules, ["DE"], Parse("Example.Movie.2020.German.1080p.BluRay.H264-BAD"), "Example.Movie.2020.German.1080p.BluRay.H264-BAD");
            Assert.AreEqual("LanguageGroupBlocked", blockedGroup.Blocked!.Code);

            var blockedWord = LanguageRuleLogic.Apply(rules, ["DE"], Parse("Example.Movie.2020.German.CAM.H264-ZZZ"), "Example.Movie.2020.German.CAM.H264-ZZZ");
            Assert.AreEqual("LanguageWordBlocked", blockedWord.Blocked!.Code);
            Assert.AreEqual("CAM", blockedWord.Blocked.Value);

            // The same group in another language than the rule's earns nothing and blocks nothing.
            var otherLanguage = LanguageRuleLogic.Apply(rules, ["FR"], Parse("Example.Movie.2020.French.Remux.1080p.BluRay.H264-GRP"), "Example.Movie.2020.French.Remux.1080p.BluRay.H264-GRP");
            Assert.AreEqual(0, otherLanguage.Points.Count);
            Assert.IsNull(otherLanguage.Blocked);
        }
    }
}
