using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    // A book release that names no language is in the language its title is known in: a localized edition title, or the original title.
    [TestClass]
    public sealed class BookLanguageFromTitleTests
    {
        private static readonly (string Language, string Title)[] Known =
        [
            ("de", "Stolz und Vorurteil"), ("en", "Pride and Prejudice"), ("fr", "Orgueil et Préjugés"), ("de", "Emma"), ("en", "Emma")
        ];

        private static QualityProfile Profile(LanguageFallbackType fallback = LanguageFallbackType.Original) =>
            new("profile", "Books", ["EPUB"], ["EPUB"], false, null, null, null) { IsBook = true, LanguageOrder = ["de"], OriginalLanguage = "EN", LanguageFallback = fallback };

        private static (SelectionCandidate Candidate, ReleaseJudgement<BookIdentityMatch> Judged) Candidate(string release, string title = "Pride and Prejudice", string[]? editionTitles = null)
        {
            var offer = new ReleaseOffer(release, 1, "Usenet", 25, release, new Uri("https://example.org/" + Uri.EscapeDataString(release)), null, 1_000_000, null, ["title"]);
            var judged = BookReleaseJudge.Judge([title, .. editionTitles ?? ["Stolz und Vorurteil"]], "Jane Austen", offer, "EN",
                new BookSearch(false, new Dictionary<string, QualitySize>(), true) { LanguageTitles = Known });
            return (new SelectionCandidate(offer.Identity, judged.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judged.Evidence, judged.Coverage)
            {
                Languages = judged.Languages ?? [],
                LanguagesFromTitle = judged.LanguagesFromTitle
            }, judged);
        }

        [TestMethod]
        public void LocalizedEditionTitle_CountsAsThatLanguage_AndIsTakenWhenItIsRequested()
        {
            var (candidate, judged) = Candidate("Jane Austen - Stolz und Vorurteil (epub)");

            CollectionAssert.AreEqual(new[] { "DE" }, judged.Languages!.ToArray());
            Assert.IsTrue(judged.LanguagesFromTitle);
            var result = ReleaseSelectionEngine.Select(Profile(), [candidate]);
            Assert.AreEqual(candidate.Id, result.Winner!.Candidate.Id);
            Assert.IsTrue(result.Winner.Candidate.LanguagesFromTitle);
        }

        [TestMethod]
        public void OriginalTitle_CountsAsTheOriginalLanguage_AllowedWhereTheProfileSaysSo()
        {
            // The Work is a Japanese light novel; its original title carries the original language.
            var offer = new ReleaseOffer("Reidai Shousetsu v01 (epub)", 1, "Nyaa", 25, "r", new Uri("https://example.org/r"), null, 1_000_000, null, ["title"]);
            var search = new BookSearch(false, new Dictionary<string, QualitySize>(), true) { LanguageTitles = [("ja", "Reidai Shousetsu"), ("en", "Example Novel")] };
            var judged = BookReleaseJudge.Judge(["Example Novel"], null, offer, "JA", search);
            var candidate = new SelectionCandidate(offer.Identity, judged.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judged.Evidence, judged.Coverage)
                { Languages = judged.Languages ?? [], LanguagesFromTitle = judged.LanguagesFromTitle };
            var profile = new QualityProfile("profile", "Books", ["EPUB"], ["EPUB"], false, null, null, null) { IsBook = true, LanguageOrder = ["en"], OriginalLanguage = "JA", LanguageFallback = LanguageFallbackType.Original };

            Assert.AreEqual(BookIdentityMatch.WrongTitle, judged.Match);
            judged = BookReleaseJudge.Judge(["Reidai Shousetsu"], null, offer, "JA", search);
            candidate = new SelectionCandidate(offer.Identity, judged.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judged.Evidence, judged.Coverage)
                { Languages = judged.Languages ?? [], LanguagesFromTitle = judged.LanguagesFromTitle };
            CollectionAssert.AreEqual(new[] { "JA" }, judged.Languages!.ToArray());
            Assert.IsTrue(judged.LanguagesFromTitle);
            Assert.AreEqual(candidate.Id, ReleaseSelectionEngine.Select(profile, [candidate]).Winner!.Candidate.Id);

            // Without the original as a fallback the profile leaves it to a person, with the language it found.
            profile = new QualityProfile("profile", "Books", ["EPUB"], ["EPUB"], false, null, null, null) { IsBook = true, LanguageOrder = ["en"], OriginalLanguage = "JA", LanguageFallback = LanguageFallbackType.None };
            var strict = ReleaseSelectionEngine.Select(profile, [candidate]);
            Assert.IsNull(strict.Winner);
            Assert.AreEqual(("LanguageNotAllowed", "JA"), (strict.Ranked.Single().Reasons[^1].Code, strict.Ranked.Single().Reasons[^1].Value));
        }

        [TestMethod]
        public void EditionTitleOfTheOriginalLanguage_IsTakenWhereTheProfileAllowsTheOriginal()
        {
            var (candidate, judged) = Candidate("Jane Austen - Pride and Prejudice (epub)");

            CollectionAssert.AreEqual(new[] { "EN" }, judged.Languages!.ToArray());
            Assert.AreEqual(candidate.Id, ReleaseSelectionEngine.Select(Profile(), [candidate]).Winner!.Candidate.Id);
            Assert.IsNull(ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.None), [candidate]).Winner);
        }

        [TestMethod]
        public void MainTitleAlone_SaysNothingAboutTheLanguage()
        {
            // No edition or original title carries it: it may be the original or a translated title.
            var offer = new ReleaseOffer("Jane Austen - Northanger Abbey (epub)", 1, "Usenet", 25, "n", new Uri("https://example.org/n"), null, 1_000_000, null, ["title"]);
            var judged = BookReleaseJudge.Judge(["Northanger Abbey"], "Jane Austen", offer, "EN", new BookSearch(false, new Dictionary<string, QualitySize>(), true) { LanguageTitles = Known });

            Assert.AreEqual(0, judged.Languages!.Count);
            Assert.IsFalse(judged.LanguagesFromTitle);
        }

        [TestMethod]
        public void TitleKnownInSeveralLanguages_IsAmbiguous_ManualReview()
        {
            var (candidate, judged) = Candidate("Jane Austen - Emma (epub)", "Emma", []);

            Assert.AreEqual(0, judged.Languages!.Count);
            Assert.IsFalse(judged.LanguagesFromTitle);
            var result = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Any), [candidate]);
            Assert.IsNull(result.Winner);
            Assert.AreEqual("LanguageUnknown", result.Ranked.Single().Reasons[^1].Code);
        }

        [TestMethod]
        public void TitleOfNoKnownLanguage_StaysUnknown_ManualReview()
        {
            var (candidate, judged) = Candidate("Jane Austen - Orgullo y prejuicio (epub)", "Pride and Prejudice");

            Assert.AreEqual(BookIdentityMatch.WrongTitle, judged.Match);
            Assert.AreEqual(0, judged.Languages!.Count);
            Assert.IsNull(ReleaseSelectionEngine.Select(Profile(), [candidate]).Winner);
        }

        [TestMethod]
        public void NamedLanguage_IsNeverReplacedByTheTitle()
        {
            var (_, judged) = Candidate("Jane Austen - Stolz und Vorurteil (EN) (epub)");

            CollectionAssert.AreEqual(new[] { "EN" }, judged.Languages!.ToArray());
            Assert.IsFalse(judged.LanguagesFromTitle);
        }

        [TestMethod]
        public void TitleIsCompared_WholeAfterTheSameCleaning_AsTheSeriesMatcher()
        {
            // Accents, case and punctuation do not matter; extra words do.
            Assert.IsTrue(Candidate("Jane Austen - STOLZ UND VORURTEIL (epub)").Judged.LanguagesFromTitle);
            Assert.IsTrue(Candidate("Jane.Austen-Stolz.und.Vorurteil.2019.RETAIL.EPUB-GRP").Judged.LanguagesFromTitle);
            Assert.IsFalse(Candidate("Jane Austen - Stolz und Vorurteil Hörspiel Extras (epub)").Judged.LanguagesFromTitle);
        }
    }
}
