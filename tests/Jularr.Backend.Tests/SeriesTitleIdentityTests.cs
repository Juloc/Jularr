using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Backend.Tests
{
    // A release is a series' only when it names that series whole, as Sonarr's parser decides; a longer title that contains the words is another series.
    [TestClass]
    public sealed class SeriesTitleIdentityTests
    {
        private static ReleaseOffer Offer(string title, params string[] stages) =>
            new(title, 1, "Indexer", 25, "g", new Uri("https://indexer.example.test/g"), null, 2_000_000_000, DateTimeOffset.UtcNow, stages.Length == 0 ? ["title"] : stages);

        private static UnitScope Season(int season, int episode)
        {
            UnitStateData[] states = [new(season * 100 + episode, season, episode, true, false, false, episode, null)];
            return AcquisitionSelectionLogic.ReadUnitScope(states, season, episode);
        }

        private static ReleaseJudgement<VideoIdentityMatch> Judge(string[] titles, int? year, string release, AcquisitionKind kind = AcquisitionKind.Tv, int season = 1, int episode = 1, params string[] stages) =>
            VideoReleaseJudge.Judge(kind, titles, year, Season(season, episode), Offer(release, stages));

        [TestMethod]
        [DataRow("Peaky Blinders", 2013, "Peaky.Blinders.S01E01.1080p.WEB-DL.DD5.1.H.264-NTb", true)]
        [DataRow("Peaky Blinders", 2013, "Peaky.Blinders.2013.S01E01.1080p.WEB-DL.H.264-NTb", true)]
        [DataRow("Peaky Blinders", 2013, "Peaky.Blinders.Gangs.of.Birmingham.S01E01.1080p.WEB-DL.H.264-NTb", false)]
        [DataRow("Peaky Blinders", 2013, "Peaky Blinders - Gangs of Birmingham - S01E01 - 1080p WEB-DL", false)]
        [DataRow("House of the Dragon", 2022, "House.of.the.Dragon.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP", true)]
        [DataRow("House of the Dragon", 2022, "House.of.the.Dragon.S01E01.German.DL.1080p.WEB.h264-WvF", true)]
        [DataRow("House of the Dragon", 2022, "House.of.the.Dragon.Inside.the.Episode.S01E01.1080p.WEB.h264-GRP", false)]
        [DataRow("The Office", 2005, "Office.S01E01.1080p.WEB-DL-GRP", true)]
        [DataRow("Tom & Jerry", 1940, "Tom.and.Jerry.S01E01.1080p.WEB-DL-GRP", true)]
        [DataRow("S.W.A.T.", 2017, "SWAT.2017.S01E01.1080p.WEB-DL-GRP", true)]
        [DataRow("Amélie's Show", 2010, "Amelies.Show.S01E01.720p.HDTV-GRP", true)]
        public void Tv_ParsedTitleMustBeTheWholeSeriesTitle(string title, int year, string release, bool accepted)
        {
            var judged = Judge([title], year, release);
            Assert.AreEqual(accepted, judged.Evidence.Confidence != IdentityConfidence.Conflict, release + " -> " + judged.Match);
            if (!accepted)
            {
                Assert.AreEqual("WrongTitle", judged.Evidence.Code);
            }
        }

        [TestMethod]
        [DataRow("[SubsPlease] My Hero Academia - 05 (1080p) [ABCD1234].mkv", true)]
        [DataRow("[Erai-raws] My Hero Academia - 05 [1080p][Multiple Subtitle]", true)]
        [DataRow("[SubsPlease] My Hero Academia Memories - 05 (1080p) [ABCD1234].mkv", false)]
        [DataRow("[SubsPlease] My Hero Academia Vigilantes - 05 (1080p) [ABCD1234].mkv", false)]
        [DataRow("[SubsPlease] My Hero Academia - Vigilantes - 05 (1080p) [ABCD1234].mkv", false)]
        [DataRow("[SubsPlease] Boku no Hero Academia - 05 (1080p) [ABCD1234].mkv", true)]
        public void Anime_AliasesCountButLongerTitlesDoNot(string release, bool accepted)
        {
            var judged = Judge(["My Hero Academia", "Boku no Hero Academia"], 2016, release, AcquisitionKind.Anime, 1, 5);
            Assert.AreEqual(accepted, judged.Evidence.Confidence != IdentityConfidence.Conflict, release + " -> " + judged.Match);
        }

        [TestMethod]
        [DataRow("Great Pretender", "[Judas] Great Pretender - S01E01 [1080p]", true)]
        [DataRow("Great Pretender", "[Judas] Great Pretender razbliuto - S01E01 [1080p]", false)]
        [DataRow("Great Pretender razbliuto", "[Judas] Great Pretender razbliuto - S01E01 [1080p]", true)]
        [DataRow("Great Pretender razbliuto", "[Judas] Great Pretender - S01E01 [1080p]", false)]
        [DataRow("BanG Dream!", "[Grp] BanG Dream! - S01E01 [1080p]", true)]
        [DataRow("BanG Dream!", "[Grp] BanG Dream! It's MyGO!!!!! - S01E01 [1080p]", false)]
        [DataRow("BanG Dream! It's MyGO!!!!!", "[Grp] BanG Dream! It's MyGO!!!!! - S01E01 [1080p]", true)]
        [DataRow("BanG Dream! It's MyGO!!!!!", "[Grp] BanG Dream! - S01E01 [1080p]", false)]
        public void Anime_ConfusableTitlesAreDistinguished(string title, string release, bool accepted)
        {
            var judged = Judge([title], null, release, AcquisitionKind.Anime);
            Assert.AreEqual(accepted, judged.Evidence.Confidence != IdentityConfidence.Conflict, release + " -> " + judged.Match);
        }

        [TestMethod]
        public void Year_AddedByTheReleaseMustBeTheSeriesOwn()
        {
            var original = Judge(["Doctor Who"], 1963, "Doctor.Who.2005.S01E01.1080p.WEB-DL-GRP");
            Assert.AreEqual(VideoIdentityMatch.WrongYear, original.Match);
            Assert.AreEqual("WrongYear", original.Evidence.Code);

            var revival = Judge(["Doctor Who"], 2005, "Doctor.Who.2005.S01E01.1080p.WEB-DL-GRP");
            Assert.AreEqual(VideoIdentityMatch.Matches, revival.Match);

            // A year inside the requested title belongs to the title; one year of drift (air date versus premiere) is fine.
            Assert.AreEqual(VideoIdentityMatch.Matches, Judge(["Doctor Who (2005)"], 2005, "Doctor.Who.2005.S01E01.1080p.WEB-DL-GRP").Match);
            Assert.AreEqual(VideoIdentityMatch.Matches, Judge(["Doctor Who"], 2005, "Doctor.Who.2006.S01E01.1080p.WEB-DL-GRP").Match);
            Assert.AreEqual(VideoIdentityMatch.WrongYear, Judge(["Shameless"], 2011, "Shameless.2004.S01E01.1080p.WEB-DL-GRP").Match);
        }

        [TestMethod]
        public void Country_IsPartOfTheTitle()
        {
            Assert.AreEqual(VideoIdentityMatch.WrongTitle, Judge(["Shameless"], 2004, "Shameless.US.S01E01.1080p.WEB-DL-GRP").Match);
            Assert.AreEqual(VideoIdentityMatch.Matches, Judge(["Shameless", "Shameless US"], 2011, "Shameless.US.S01E01.1080p.WEB-DL-GRP").Match);
        }

        [TestMethod]
        public void FoundById_ADifferentNameStaysAmbiguousForAPerson_NeverTakenAutomatically()
        {
            var judged = Judge(["Peaky Blinders"], 2013, "Peaky.Blinders.Gangs.of.Birmingham.S01E01.1080p.WEB-DL-GRP", stages: "id");
            Assert.AreEqual(VideoIdentityMatch.AmbiguousTitle, judged.Match);
            Assert.AreEqual(IdentityConfidence.Ambiguous, judged.Evidence.Confidence);
        }

        [TestMethod]
        public void Season_AnotherSeasonIsStillRejected()
        {
            Assert.AreEqual(VideoIdentityMatch.WrongSeason, Judge(["House of the Dragon"], 2022, "House.of.the.Dragon.S02E05.1080p.WEB-DL-GRP").Match);
            Assert.AreEqual(VideoIdentityMatch.Matches, Judge(["House of the Dragon"], 2022, "House.of.the.Dragon.S02E05.1080p.WEB-DL-GRP", season: 2, episode: 5).Match);
        }

        [TestMethod]
        public void Selection_ConfusableReleaseIsRejectedWithItsReason_TheRightOneIsTaken()
        {
            var profile = new QualityProfile("1", "Test", ["WEBDL-1080p"], ["WEBDL-1080p"], false, null, null, null);
            var target = new[] { "Peaky.Blinders.S01E01.1080p.WEB-DL.H.264-AAA", "Peaky.Blinders.Gangs.of.Birmingham.S01E01.1080p.WEB-DL.H.264-ZZZ" }
                .Select(title => (Release: title, Judged: Judge(["Peaky Blinders"], 2013, title))).ToArray();
            var result = ReleaseSelectionEngine.Select(profile, target.Select(item => new SelectionCandidate(item.Release, item.Judged.Parsed, 2_000_000_000, "Indexer", 25, DateTimeOffset.UtcNow,
                item.Judged.Evidence, item.Judged.Coverage)).ToArray());

            Assert.AreEqual("Peaky.Blinders.S01E01.1080p.WEB-DL.H.264-AAA", result.Winner!.Candidate.Id);
            var rejected = result.Ranked.Single(evaluation => evaluation.Candidate.Id.Contains("Gangs"));
            Assert.AreEqual(SelectionDecision.Rejected, rejected.Decision);
            Assert.IsTrue(rejected.Reasons.Any(reason => reason.Code == "WrongTitle" && reason.Value == "Peaky Blinders Gangs of Birmingham"));
        }

        [TestMethod]
        public void Import_FilesOfAnotherSeriesAreWrongSeries_AliasesAndYearsAreKnown()
        {
            ImportVerdict Verdict(string file) => ImportJudgement.Judge(MediaType.Series, MediaSubtype.Anime, ["My Hero Academia", "Boku no Hero Academia"], 2016, "[Grp] My Hero Academia - 05",
                1, 5, 5, [new StorageFile(file, 1_000_000_000, DateTime.UtcNow)], [new TargetUnit(105, 1, 5, false, 5)], null);

            Assert.AreEqual(ImportVerdictKind.Discard, Verdict("[Grp] My Hero Academia Memories - 05 [1080p].mkv").Kind);
            Assert.AreEqual("import_wrong_series", Verdict("[Grp] My Hero Academia Memories - 05 [1080p].mkv").ReasonKey);
            Assert.AreNotEqual(ImportVerdictKind.Discard, Verdict("[Grp] Boku no Hero Academia - 05 [1080p].mkv").Kind);
            Assert.AreNotEqual(ImportVerdictKind.Discard, Verdict("[Grp] My Hero Academia - 05 [1080p].mkv").Kind);
        }
    }
}
