using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Tests
{
    // A book is wanted once per language: each language is searched, taken and kept on its own.
    [TestClass]
    public sealed class BookLanguagePerEditionTests
    {
        private const string GermanFile = """[{"Quality":"EPUB","WorkUnitId":null,"Facts":{"ReleaseName":"Jane Austen - Pride and Prejudice (DE) (epub)","SizeBytes":1000000,"IndexerFlags":[],"Movie":false,"OriginalLanguage":"en","Year":1813,"MediaTypeId":3}}]""";

        private static WantedTargetData Target(string[] wanted, string[] missing) =>
            new(1, 1, MediaType.Book, null, "Pride and Prejudice", 1813, [], 1, "Books", "{}", [], false, true, false, 0, [], [], "Jane Austen", null, null, GermanFile, originalLanguage: "en",
                wantedLanguages: wanted, missingLanguages: missing);

        private static WantedTargetData Chained(string[] chains, string[] wanted, string[] missing) =>
            new(1, 1, MediaType.Book, null, "Pride and Prejudice", 1813, [], 1, "Books", """{"AllowedQualities":["EPUB","PDF"],"QualityOrder":["EPUB","PDF"],"UpgradeAllowed":false}""", [], false, true, false, 0, [],
                [], "Jane Austen", null, null, GermanFile, originalLanguage: "en", wantedLanguages: wanted, missingLanguages: missing, languageChains: chains);

        private static QualityProfile Profile(string[] order, string[] wanted) =>
            new("1", "Books", ["EPUB", "PDF"], ["EPUB", "PDF"], false, null, null, null)
            {
                IsBook = true,
                LanguageOrder = order,
                WantedLanguages = wanted,
                OriginalLanguage = "en",
                LanguageFallback = LanguageFallbackType.Original
            };

        private static ReleaseOffer Offer(string title) => new(title, 1, "Anna's Archive", 25, title, new Uri("https://anna.example.test/" + Uri.EscapeDataString(title)), null, 1_000_000, null, ["title"]);

        [TestMethod]
        public void GrabTargetLanguage_IsTheFirstMissingLanguageTheReleaseCarries_OrTheFirstMissingOneWhenItNamesNone()
        {
            var both = Target(["de", "id"], ["de", "id"]);
            Assert.AreEqual("id", AcquisitionSelectionLogic.TargetLanguage(both, ["ID"]));
            Assert.AreEqual("de", AcquisitionSelectionLogic.TargetLanguage(both, ["EN", "DE"]));
            Assert.AreEqual("de", AcquisitionSelectionLogic.TargetLanguage(both, []));

            // Only Indonesian is missing: a German release is for no target, one without a language is for Indonesian.
            var rest = Target(["de", "id"], ["id"]);
            Assert.IsNull(AcquisitionSelectionLogic.TargetLanguage(rest, ["DE"]));
            Assert.AreEqual("id", AcquisitionSelectionLogic.TargetLanguage(rest, []));

            // Nothing missing, or another medium: no language.
            Assert.IsNull(AcquisitionSelectionLogic.TargetLanguage(Target(["de"], []), ["DE"]));
        }

        [TestMethod]
        public void MissingLanguage_IsSearchedOnItsOwn_TheFileOfAnotherLanguageIsNoUpgradeToCompareWith()
        {
            var profile = Profile(["id"], ["de", "id"]);
            var offers = new[] { Offer("Jane Austen - Pride and Prejudice (ID) (epub)"), Offer("Jane Austen - Pride and Prejudice (DE) (epub)"), Offer("Jane Austen - Pride and Prejudice (ID) (pdf)") };

            // German is present, Indonesian missing: the Indonesian EPUB is taken, the German one (already there) is only a row for a person.
            var result = AcquisitionSelectionLogic.Select(profile, Target(["de", "id"], ["id"]), AcquisitionKind.Book, null, offers);
            Assert.AreEqual("Jane Austen - Pride and Prejudice (ID) (epub)", result.Winner!.Candidate.Parsed!.RawTitle);
            Assert.AreEqual(SelectionDecision.ManualReview, result.Ranked.Single(row => row.Candidate.Languages.Contains("DE")).Decision);

            // Even a lower quality (PDF) of the missing language is not "no upgrade" of the German EPUB.
            var pdf = AcquisitionSelectionLogic.Select(profile, Target(["de", "id"], ["id"]), AcquisitionKind.Book, null, [offers[2]]);
            Assert.AreEqual(SelectionDecision.Eligible, pdf.Ranked.Single().Decision);

            // Every language covered: nothing is missing, so a release of the same quality is no upgrade.
            var covered = AcquisitionSelectionLogic.Select(Profile(["de", "id"], ["de", "id"]), Target(["de", "id"], []), AcquisitionKind.Book, null, [offers[2]]);
            Assert.AreEqual("NotAnUpgrade", covered.Ranked.Single().Reasons[^1].Code);
        }

        [TestMethod]
        public void Import_AFileOfAnotherWantedLanguageIsKept_OnlyAFileInANeitherWantedLanguageGoes()
        {
            ReplacedFileData File(string earlier, string replacing) => new(1, 1, 1, "books", "book.epub", "EPUB", 1, "EPUB", 1, Evidence(earlier), Evidence(replacing));
            static string Evidence(string name) => $$"""{"ReleaseName":"{{name}}","SizeBytes":1000000,"IndexerFlags":[],"Movie":false,"OriginalLanguage":"en","Year":1813,"MediaTypeId":3}""";
            var profile = Profile(["de"], ["de", "id"]);

            // The German file stays when the Indonesian one arrives, and the other way round, whatever the order of the languages.
            Assert.IsFalse(ReplacementLogic.IsBetter(profile, File("Pride and Prejudice (DE) (epub)", "Pride and Prejudice (ID) (epub)")));
            Assert.IsFalse(ReplacementLogic.IsBetter(profile, File("Pride and Prejudice (ID) (epub)", "Pride and Prejudice (DE) (epub)")));

            // An English file nobody wants is replaced by a wanted one; a wanted one is not replaced by an English one.
            Assert.IsTrue(ReplacementLogic.IsBetter(profile, File("Pride and Prejudice (EN) (epub)", "Pride and Prejudice (ID) (epub)")));
            Assert.IsFalse(ReplacementLogic.IsBetter(profile, File("Pride and Prejudice (ID) (epub)", "Pride and Prejudice (EN) (epub)")));
        }

        [TestMethod]
        public void ProfileLanguages_AreTheChainsOfTheMissingLanguages_FirstThenSecond_OriginalOnlyWhereAChainAllowsIt()
        {
            string[] chains = ["de|en|1", "id||0"];

            // Both languages are missing: both chains, firsts then seconds; one chain allows the original.
            var both = AcquisitionSelectionLogic.ReadProfile(Chained(chains, ["de", "id"], ["de", "id"]));
            CollectionAssert.AreEqual(new[] { "de", "id", "en" }, both.LanguageOrder);
            Assert.AreEqual(LanguageFallbackType.Original, both.LanguageFallback);

            // Only Indonesian is missing: its chain alone (no second language, no original).
            var indonesian = AcquisitionSelectionLogic.ReadProfile(Chained(chains, ["de", "id"], ["id"]));
            CollectionAssert.AreEqual(new[] { "id" }, indonesian.LanguageOrder);
            Assert.AreEqual(LanguageFallbackType.None, indonesian.LanguageFallback);

            // Everything covered: all chains again (a manual search offers every language).
            CollectionAssert.AreEqual(new[] { "de", "id", "en" }, AcquisitionSelectionLogic.ReadProfile(Chained(chains, ["de", "id"], [])).LanguageOrder);
        }

        [TestMethod]
        public void GrabTargetLanguage_ASecondLanguageOrTheOriginalStandsInForTheFirstLanguageOfItsChain()
        {
            var target = Chained(["de|en|1", "id||0"], ["de", "id"], ["de", "id"]);

            // English is the second language of the German chain: it covers German until a German release comes; the original (en) too; Indonesian has neither.
            Assert.AreEqual("de", AcquisitionSelectionLogic.TargetLanguage(target, ["EN"]));
            Assert.AreEqual("id", AcquisitionSelectionLogic.TargetLanguage(target, ["ID"]));
            Assert.IsNull(AcquisitionSelectionLogic.TargetLanguage(target, ["PL"]));

            // Indonesian alone is missing and its chain has no second language and no original: an English release is for no target.
            Assert.IsNull(AcquisitionSelectionLogic.TargetLanguage(Chained(["de|en|1", "id||0"], ["de", "id"], ["id"]), ["EN"]));
        }
    }
}
