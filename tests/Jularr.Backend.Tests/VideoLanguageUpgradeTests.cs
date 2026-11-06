using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Tests
{
    // A film or series file replaces another by language before quality: a release with more of the chains' first languages, or one in an earlier step, replaces it (through the feed);
    // one that serves the chains worse never does. A Multi release with all first languages wins among the releases of a search.
    [TestClass]
    public sealed class VideoLanguageUpgradeTests
    {
        private const string Definition = """{"AllowedQualities":["Bluray-1080p","WEBDL-1080p"],"QualityOrder":["Bluray-1080p","WEBDL-1080p"],"UpgradeAllowed":true,"UpgradeCutoffQuality":"Bluray-1080p"}""";

        private static string File(string name) =>
            "[{\"Quality\":\"WEBDL-1080p\",\"WorkUnitId\":null,\"Facts\":{\"ReleaseName\":\"" + name + "\",\"SizeBytes\":4000000000,\"IndexerFlags\":[],\"Movie\":true,\"OriginalLanguage\":\"ja\",\"Year\":2020,\"MediaTypeId\":1}}]";

        private static WantedTargetData Target(string installed, params string[] chains) =>
            new(1, 1, MediaType.Movie, null, "Example Movie", 2020, [], 1, "Movies", Definition, [], false, true, false, 0, [], [], null, null, null, installed, originalLanguage: "ja", languageChains: chains);

        private static ReleaseOffer Offer(string title) =>
            new(title, 1, "Indexer", 0, title, new Uri("https://example.org/" + Uri.EscapeDataString(title)), null, 4_000_000_000, null, ["feed"]);

        private static SelectionResult Select(WantedTargetData target, params string[] titles) =>
            AcquisitionSelectionLogic.Select(AcquisitionSelectionLogic.ReadProfile(target), target, AcquisitionKind.Movie, null, titles.Select(Offer), automatic: true);

        private static CandidateEvaluation Row(SelectionResult result, string title) => result.Ranked.Single(evaluation => evaluation.Candidate.Parsed?.RawTitle == title);

        private const string Original = "Example.Movie.2020.1080p.WEB-DL.H264-AAA";
        private const string German = "Example.Movie.2020.German.1080p.WEB-DL.H264-BBB";

        [TestMethod]
        public void FileInTheOriginalLanguage_IsReplacedByAReleaseInTheFirstLanguage_OnlyThroughTheFeedDecision()
        {
            var target = Target(File(Original), "de||1");

            var result = Select(target, German, "Example.Movie.2020.1080p.WEB-DL.H264-CCC");

            Assert.AreEqual(German, result.Winner!.Candidate.Parsed!.RawTitle);
            Assert.AreEqual(SelectionDecision.Eligible, Row(result, German).Decision);
            // The same audio again is no upgrade.
            Assert.IsNull(Select(target, "Example.Movie.2020.1080p.WEB-DL.H264-CCC").Winner);
            Assert.IsTrue(AcquisitionSelectionLogic.NeedsUpgrade(AcquisitionSelectionLogic.ReadProfile(target), AcquisitionSelectionLogic.Installed(AcquisitionSelectionLogic.ReadProfile(target), target)!.Value));
        }

        [TestMethod]
        public void FileInTheFirstLanguage_IsNeverReplacedByABetterQualityInAnotherLanguage()
        {
            var target = Target(File(German), "de||1");

            var result = Select(target, "Example.Movie.2020.1080p.BluRay.H264-AAA");

            Assert.IsNull(result.Winner);
            Assert.AreEqual("NotAnUpgrade", Row(result, "Example.Movie.2020.1080p.BluRay.H264-AAA").Reasons[^1].Code);
            // A file that covers every first language needs no upgrade for its language.
            Assert.IsFalse(AcquisitionSelectionLogic.NeedsUpgrade(AcquisitionSelectionLogic.ReadProfile(Target(File(German), "de||1")), ("Bluray-1080p", 0, ["de"], false)));
        }

        [TestMethod]
        public void SeveralFirstLanguages_AMultiReleaseWins_AndReplacesTheFileOfOneLanguage()
        {
            var target = Target(File(German), "de||1", "id||1");
            var multi = "Example.Movie.2020.German.Indonesian.1080p.WEB-DL.H264-DDD";

            var result = Select(target, multi, "Example.Movie.2020.Indonesian.1080p.WEB-DL.H264-EEE");

            Assert.AreEqual(multi, result.Winner!.Candidate.Parsed!.RawTitle);
            // The file with one first language is open for the other; a single-language release of the other is no replacement of it.
            Assert.IsTrue(AcquisitionSelectionLogic.NeedsUpgrade(AcquisitionSelectionLogic.ReadProfile(target), ("WEBDL-1080p", 0, ["de"], false)));
            Assert.IsNull(Select(target, "Example.Movie.2020.Indonesian.1080p.WEB-DL.H264-EEE").Winner);

            // In a search without a file, the Multi release also ranks before either single language.
            var fresh = Select(Target(null!, "de||1", "id||1"), German, multi, "Example.Movie.2020.Indonesian.1080p.WEB-DL.H264-EEE");
            Assert.AreEqual(multi, fresh.Winner!.Candidate.Parsed!.RawTitle);
            Assert.AreEqual("PreferredLanguage", fresh.WinnerReason!.Code);
        }

        [TestMethod]
        public void FileWithoutRecordedAudio_CoversEveryLanguage()
        {
            var target = Target("[{\"Quality\":\"WEBDL-1080p\",\"WorkUnitId\":null,\"Facts\":{\"ReleaseName\":\"Example Movie\",\"SizeBytes\":1,\"IndexerFlags\":[],\"Movie\":true,\"OriginalLanguage\":null,\"Year\":2020,\"MediaTypeId\":1}}]", "de||1");
            var profile = AcquisitionSelectionLogic.ReadProfile(target);

            Assert.IsFalse(AcquisitionSelectionLogic.NeedsUpgrade(profile, ("Bluray-1080p", 0, [], false)));
            Assert.IsFalse(UpgradePolicy.IsBetterLanguage(profile, [], ["ja"]));
        }

        [TestMethod]
        public void Replacement_AtImport_PutsTheLanguageBeforeTheQuality()
        {
            var profile = AcquisitionSelectionLogic.ReadProfile(Target(File(Original), "de||1"));
            ReplacedFileData Replaced(string earlierName, string earlierQuality, string newName, string newQuality) => new(1, 2, 1, "/films", "a.mkv", earlierQuality, 1, newQuality, 2, Facts(earlierName), Facts(newName));
            string Facts(string name) => "{\"ReleaseName\":\"" + name + "\",\"SizeBytes\":4000000000,\"IndexerFlags\":[],\"Movie\":true,\"OriginalLanguage\":\"ja\",\"Year\":2020,\"MediaTypeId\":1}";

            // A lower quality in the first language replaces the original-language file; a better quality in the original language does not replace the file in the first.
            Assert.IsTrue(ReplacementLogic.IsBetter(profile, Replaced(Original, "Bluray-1080p", German, "WEBDL-1080p")));
            Assert.IsFalse(ReplacementLogic.IsBetter(profile, Replaced(German, "WEBDL-1080p", "Example.Movie.2020.1080p.BluRay.H264-AAA", "Bluray-1080p")));
        }
    }
}
