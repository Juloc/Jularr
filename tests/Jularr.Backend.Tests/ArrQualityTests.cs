using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArrQualityTests
    {
        [TestMethod]
        public void InvalidFormatPatterns_RejectTheCandidate_EvenWhenNegatedOrScoredNegatively()
        {
            var format = Format("Broken", new FormatCondition("Broken", FormatConditionKind.ReleaseTitle, true, true) { Value = "(" });
            var profile = new QualityProfile("1", "Test", ["WEBDL-1080p"], ["WEBDL-1080p"], false, null, null, null)
            {
                Formats = [(format, -10000)]
            };
            var result = Score(profile, "Example.Show.S01E01.1080p.WEB-DL.x264-GRP");
            Assert.IsFalse(result.Accepted);
            Assert.AreEqual("FormatEvaluationFailed", result.RejectionReasons.Single().Code);
        }

        // A release gets the quality Sonarr gives it for series and Radarr for films.
        [TestMethod]
        [DataRow("Example.Show.S01E01.1080p.WEB.h264-GRP", "WEBDL-1080p", "WEBDL-1080p")]
        [DataRow("Example.Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP", "WEBDL-1080p", "WEBDL-1080p")]
        [DataRow("Example.Show.S01E01.1080p.WEBRip.x264-GRP", "WEBRip-1080p", "WEBRip-1080p")]
        [DataRow("Example.Show.S01E01.WEB-DL.x264-GRP", "WEBDL-480p", "WEBDL-480p")]
        [DataRow("Example.Movie.2020.1080p.BluRay.REMUX.AVC.DTS-HD.MA.5.1-GRP", "Bluray-1080p Remux", "Remux-1080p")]
        [DataRow("Example.Movie.2020.2160p.UHD.BluRay.x265-GRP", "Bluray-2160p", "Bluray-2160p")]
        [DataRow("Example.Movie.2020.576p.BluRay.x264-GRP", "Bluray-576p", "Bluray-576p")]
        [DataRow("Example.Movie.2020.COMPLETE.UHD.BLURAY-GRP", "Bluray-720p", "BR-DISK")]
        [DataRow("Example.Show.S01E01.720p.HDTV.x264-GRP", "HDTV-720p", "HDTV-720p")]
        [DataRow("Example.Show.S01E01.HDTV.x264-GRP", "SDTV", "SDTV")]
        [DataRow("Example.Show.S01E01.PDTV.x264-GRP", "SDTV", "SDTV")]
        [DataRow("Example.Show.S01E01.1080p.x264-GRP", "HDTV-1080p", "HDTV-1080p")]
        [DataRow("Example.Movie.2020.1080i.HDTV.MPEG2-GRP", "Raw-HD", "Raw-HD")]
        [DataRow("Example.Movie.2020.DVDRip.x264-GRP", "DVD", "DVD")]
        [DataRow("Example.Movie.2020.DVD9-GRP", "DVD", "DVD-R")]
        [DataRow("Example.Movie.2020.DVDSCR.x264-GRP", "DVD", "DVDSCR")]
        [DataRow("Example.Movie.2020.R5.x264-GRP", "Unknown", "REGIONAL")]
        [DataRow("Example.Movie.2020.HDCAM.x264-GRP", "Unknown", "CAM")]
        [DataRow("Example.Movie.2020.HDTS.x264-GRP", "Unknown", "TELESYNC")]
        [DataRow("Example.Movie.2020.TELECINE.x264-GRP", "Unknown", "TELECINE")]
        [DataRow("Example.Movie.2020.WORKPRINT.x264-GRP", "Unknown", "WORKPRINT")]
        public void Release_GetsTheQualityNameOfSonarrAndRadarr(string title, string series, string movie)
        {
            Assert.IsTrue(ReleaseParser.TryParse(title, out var release), title);
            Assert.AreEqual(series, ReleaseQuality.GetKey(release), title);
            Assert.AreEqual(movie, ReleaseQuality.GetKey(release, movie: true), title);
            CollectionAssert.Contains(ProfileQualities.For(MediaType.Series, null).ToArray(), series);
            CollectionAssert.Contains(ProfileQualities.For(MediaType.Movie, null).ToArray(), movie);
        }

        // Languages count behind the title; a name that states none is English.
        [TestMethod]
        [DataRow("Example.Film.2020.German.DL.1080p.WEB.h264-GRP", "de")]
        [DataRow("Example.Show.S01E01.GERMAN.1080p.WEB.h264-GRP", "de")]
        [DataRow("Example.Show.S01E01.ITA.ENG.1080p.WEB.h264-GRP", "it en")]
        [DataRow("Example.Film.2020.MULTi.TRUEFRENCH.1080p.BluRay.x264-GRP", "fr")]
        [DataRow("The.German.Doctor.2013.1080p.BluRay.x264-GRP", "en")]
        [DataRow("Dan.Da.Dan.S01E01.1080p.WEB.h264-GRP", "en")]
        [DataRow("Example.Show.S01E01.1080p.WEB.h264-GRP", "en")]
        public void Release_StatesItsLanguagesBehindTheTitle(string title, string languages)
        {
            Assert.IsTrue(ReleaseParser.TryParse(title, out var release), title);
            Assert.AreEqual(languages, string.Join(' ', release.Languages), title);
        }

        // Custom formats match as in Sonarr: required conditions must hold, of each kind at least one, negated ones must not match.
        [TestMethod]
        public void CustomFormats_MatchAsSonarrDecides()
        {
            var german = Format("German", new FormatCondition("German", FormatConditionKind.Language, false, true) { Value = "de" });
            var original = Format("Original", new FormatCondition("Original", FormatConditionKind.Language, false, true) { Value = "original" });
            var notEnglish = Format("Not English", new FormatCondition("English", FormatConditionKind.Language, false, true) { Value = "en", ExceptLanguage = true });
            var x265 = Format("x265 without HDR", new FormatCondition("x265", FormatConditionKind.ReleaseTitle, false, true) { Value = @"[xh]\.?265" },
                new FormatCondition("HDR", FormatConditionKind.ReleaseTitle, true, true) { Value = @"\bHDR\b" });
            var groups = Format("Good groups", new FormatCondition("GRP", FormatConditionKind.ReleaseGroup, false, false) { Value = "^GRP$" },
                new FormatCondition("TEAM", FormatConditionKind.ReleaseGroup, false, false) { Value = "^TEAM$" });
            var web1080 = Format("group:1002", new FormatCondition("Web", FormatConditionKind.Source, false, true) { Value = "web" },
                new FormatCondition("1080p", FormatConditionKind.Resolution, false, true) { Resolution = 1080 });
            var small = Format("Small", new FormatCondition("Up to 2 GB", FormatConditionKind.Size, false, true) { Maximum = 2 });
            var anime = new FormatContext(false, "ja", 2020);

            Assert.IsTrue(Matches(german, "Example.Show.S01E01.GERMAN.1080p.WEB.h264-GRP", anime));
            Assert.IsFalse(Matches(german, "Example.Show.S01E01.1080p.WEB.h264-GRP", anime));
            Assert.IsTrue(Matches(original, "Example.Show.S01E01.Japanese.1080p.WEB.h264-GRP", anime));
            Assert.IsFalse(Matches(original, "Example.Show.S01E01.1080p.WEB.h264-GRP", anime));
            Assert.IsTrue(Matches(notEnglish, "Example.Show.S01E01.ITA.ENG.1080p.WEB.h264-GRP", anime));
            Assert.IsFalse(Matches(notEnglish, "Example.Show.S01E01.1080p.WEB.h264-GRP", anime));
            Assert.IsTrue(Matches(x265, "Example.Show.S01E01.1080p.WEB.x265-GRP", anime));
            Assert.IsFalse(Matches(x265, "Example.Show.S01E01.1080p.WEB.HDR.x265-GRP", anime));
            Assert.IsTrue(Matches(groups, "Example.Show.S01E01.1080p.WEB.h264-TEAM", anime));
            Assert.IsFalse(Matches(groups, "Example.Show.S01E01.1080p.WEB.h264-OTHER", anime));
            Assert.IsTrue(Matches(web1080, "Example.Show.S01E01.1080p.WEB-DL.h264-GRP", anime));
            Assert.IsFalse(Matches(web1080, "Example.Show.S01E01.720p.WEB-DL.h264-GRP", anime));
            Assert.IsFalse(Matches(web1080, "Example.Show.S01E01.1080p.WEBRip.h264-GRP", anime));
            Assert.IsTrue(Matches(small, "Example.Show.S01E01.1080p.WEB.h264-GRP", anime, 1_500_000_000));
            Assert.IsFalse(Matches(small, "Example.Show.S01E01.1080p.WEB.h264-GRP", anime, 3_000_000_000));
            Assert.IsFalse(Matches(small, "Example.Show.S01E01.1080p.WEB.h264-GRP", anime));
            var year = Format("2020", new FormatCondition("Year", FormatConditionKind.Year, false, true) { Minimum = 2019, Maximum = 2020 });
            Assert.IsTrue(Matches(year, "Example.1917.2020.1080p.WEB.h264-GRP", new FormatContext(true, "en", 2020)));
            var episode = Format("Episode", new FormatCondition("Episode", FormatConditionKind.ReleaseType, false, true) { Value = "singleEpisode" });
            Assert.IsTrue(Matches(episode, "Example.Show.S01E01.1080p.WEB.h264-GRP", anime));
            Assert.IsFalse(Matches(episode, "Example.Show.1080p.WEB.h264-GRP", anime));
        }

        // A profile scores matching formats and refuses a release below its minimum format score.
        [TestMethod]
        public void Profile_ScoresFormats_AndRefusesBelowTheMinimum()
        {
            var german = Format("German", new FormatCondition("German", FormatConditionKind.Language, false, true) { Value = "de" });
            var x265 = Format("x265", new FormatCondition("x265", FormatConditionKind.ReleaseTitle, false, true) { Value = @"[xh]\.?265" });
            var profile = new QualityProfile("1", "Test", ["WEBDL-1080p"], ["WEBDL-1080p"], false, null, null, null)
            {
                Formats = [(german, 100), (x265, -10000)],
                MinimumFormatScore = 0
            };

            var preferred = Score(profile, "Example.Show.S01E01.GERMAN.1080p.WEB.h264-GRP");
            Assert.IsTrue(preferred.Accepted);
            Assert.AreEqual(100, preferred.Score);
            var refused = Score(profile, "Example.Show.S01E01.GERMAN.1080p.WEB.x265-GRP");
            Assert.IsFalse(refused.Accepted);
            Assert.AreEqual("FormatScoreBelowMinimum", refused.RejectionReasons.Single().Code);
        }

        // Qualities of a group rank as one; within it, upgrades come from a format score higher by the increment, until the cutoff score.
        [TestMethod]
        public void Groups_RankAsOne_AndUpgradeByFormatScoreUntilTheCutoff()
        {
            var profile = new QualityProfile("1", "Test", ["Bluray-1080p", "group:1002"], ["Bluray-1080p", "group:1002", "HDTV-1080p"], true, "group:1002", null, null)
            {
                QualityGroups = [new QualityGroup(1002, "WEB 1080p", ["WEBDL-1080p", "WEBRip-1080p"])],
                CutoffFormatScore = 200,
                MinimumUpgradeFormatScore = 20
            };

            var rip = Score(profile, "Example.Show.S01E01.1080p.WEBRip.x264-GRP");
            Assert.IsTrue(rip.Accepted);
            Assert.AreEqual(Score(profile, "Example.Show.S01E01.1080p.WEB-DL.x264-GRP").QualityRank, rip.QualityRank);
            Assert.AreEqual("QualityNotAllowed", Score(profile, "Example.Show.S01E01.1080p.HDTV.x264-GRP").RejectionReasons.Single().Code);

            Assert.IsTrue(UpgradePolicy.IsUpgrade(profile, "HDTV-1080p", "WEBDL-1080p"));
            Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "WEBRip-1080p", "WEBDL-1080p"));
            Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "WEBDL-1080p", "Bluray-1080p"));
            Assert.IsTrue(UpgradePolicy.IsUpgrade(profile, "WEBDL-1080p", "Bluray-1080p", false, 0, 100));
            Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "Bluray-1080p", "WEBDL-1080p", false, 0, 100));
            Assert.IsTrue(UpgradePolicy.IsUpgrade(profile, "WEBRip-1080p", "WEBDL-1080p", false, 0, 100));
            Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "WEBRip-1080p", "WEBDL-1080p", false, 150, 160));
            Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "WEBRip-1080p", "WEBDL-1080p", false, 200, 400));
        }

        // Quality sizes scale with the runtime and the episodes of a release.
        [TestMethod]
        public void QualitySizes_LimitByRuntime()
        {
            var profile = new QualityProfile("1", "Test", ["WEBDL-1080p"], ["WEBDL-1080p"], false, null, null, null)
            {
                Sizes = new Dictionary<string, QualitySize> { ["WEBDL-1080p"] = new("WEBDL-1080p", 10, 20, 40) },
                RuntimeMinutes = 45
            };

            Assert.IsTrue(Sized(profile, "Example.Show.S01E01.1080p.WEB-DL.x264-GRP", 900).Accepted);
            Assert.AreEqual("SizeBelowQualityMinimum", Sized(profile, "Example.Show.S01E01.1080p.WEB-DL.x264-GRP", 300).RejectionReasons.Single().Code);
            Assert.AreEqual("SizeAboveQualityMaximum", Sized(profile, "Example.Show.S01E01.1080p.WEB-DL.x264-GRP", 2000).RejectionReasons.Single().Code);
            Assert.IsTrue(Sized(profile, "Example.Show.S01E01-E02.1080p.WEB-DL.x264-GRP", 3000).Accepted);
        }

        private static ReleaseScoreResult Sized(QualityProfile profile, string title, int megabytes)
        {
            Assert.IsTrue(ReleaseParser.TryParse(title, out var release), title);
            return ReleaseScorer.Score(profile, new ReleaseCandidate(release, megabytes * 1_048_576L));
        }

        // Release profiles as in Sonarr: one required term must appear and no ignored one; a term in slashes is a pattern; one bound to an indexer applies to it alone.
        [TestMethod]
        public void ReleaseProfiles_RequireAndIgnoreTerms()
        {
            var profile = new QualityProfile("1", "Test", ["WEBDL-1080p"], ["WEBDL-1080p"], false, null, null, null)
            {
                ReleaseProfiles = [new ReleaseTerms(["German", @"/\bDL\b/i"], ["x265"], null), new ReleaseTerms([], ["HDR"], 7)]
            };

            Assert.IsTrue(Indexed(profile, "Example.Show.S01E01.German.1080p.WEB-DL.x264-GRP", 3).Accepted);
            Assert.IsTrue(Indexed(profile, "Example.Show.S01E01.DL.1080p.WEB-DL.x264-GRP", 3).Accepted);
            Assert.AreEqual("MissingRequiredTerm", Indexed(profile, "Example.Show.S01E01.1080p.WEB.x264-GRP", 3).RejectionReasons.Single().Code);
            Assert.AreEqual("ContainsRejectedTerm", Indexed(profile, "Example.Show.S01E01.German.1080p.WEB-DL.x265-GRP", 3).RejectionReasons.Single().Code);
            Assert.IsTrue(Indexed(profile, "Example.Show.S01E01.German.1080p.WEB-DL.HDR.x264-GRP", 3).Accepted);
            Assert.IsFalse(Indexed(profile, "Example.Show.S01E01.German.1080p.WEB-DL.HDR.x264-GRP", 7).Accepted);
        }

        private static ReleaseScoreResult Indexed(QualityProfile profile, string title, long indexer)
        {
            Assert.IsTrue(ReleaseParser.TryParse(title, out var release), title);
            return ReleaseScorer.Score(profile, new ReleaseCandidate(release, 1_000_000_000) { IndexerId = indexer });
        }

        private static CustomFormat Format(string name, params FormatCondition[] conditions) => new(1, name, false, conditions);

        private static bool Matches(CustomFormat format, string title, FormatContext context, long? size = null)
        {
            Assert.IsTrue(ReleaseParser.TryParse(title, out var release), title);
            return CustomFormatMatcher.Matches(format, new ReleaseCandidate(release, size), context);
        }

        private static ReleaseScoreResult Score(QualityProfile profile, string title)
        {
            Assert.IsTrue(ReleaseParser.TryParse(title, out var release), title);
            return ReleaseScorer.Score(profile, new ReleaseCandidate(release, 1_000_000_000));
        }
    }
}
