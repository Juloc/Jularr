using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;

namespace Jularr.Backend.Tests
{
    // The language a book request asks for decides what an automatic search may take: the asked languages, then what the profile's fallback names; never another language, never an unknown one (the original language only where the fallback names it).
    [TestClass]
    public sealed class BookLanguageSelectionTests
    {
        private static QualityProfile Profile(LanguageFallbackType fallback, string[]? asked = null, string original = "KO") =>
            new("profile", "Books", ["EPUB", "PDF"], ["EPUB", "PDF"], false, null, null, null)
            {
                IsBook = true,
                LanguageOrder = asked ?? ["de"],
                OriginalLanguage = original,
                LanguageFallback = fallback
            };

        private static SelectionCandidate Candidate(string title, int priority = 0, string connection = "Anna's Archive")
        {
            var offer = new ReleaseOffer(title, 1, connection, priority, title, new Uri("https://example.org/" + Uri.EscapeDataString(title)), null, 1_000_000, null, ["title"]);
            // The Work's original language is not recorded, so a title alone says nothing about the language.
            var judgement = BookReleaseJudge.Judge(["Almond"], "Sun-mi Hwang", offer, null);
            return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt, judgement.Evidence, judgement.Coverage)
            {
                IndexerId = offer.ConnectionId,
                Languages = judgement.Languages ?? []
            };
        }

        // What Anna's Archive and an OPDS catalog answered for the German request of "Almond".
        private static SelectionCandidate[] NoGermanEdition(int priority = 0) =>
        [
            Candidate("Sun-mi Hwang - Almond (PL) (epub)", priority),
            Candidate("Sun-mi Hwang - Almond (ZH) (epub)", priority),
            Candidate("Sun-mi Hwang - Almond (EN) (epub)", priority),
            Candidate("Sun-mi Hwang - Almond (epub)", priority, "OPDS"),
        ];

        [TestMethod]
        public void Flexible_OnlyOtherKnownLanguages_NothingIsTakenAutomatically_EveryRowSaysWhy()
        {
            var result = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Any, original: "JA"), NoGermanEdition().Where(candidate => candidate.Languages.Count > 0).ToArray());

            Assert.IsNull(result.Winner);
            Assert.AreEqual(SelectionOutcome.ManualReviewOnly, result.Outcome);
            Assert.IsTrue(result.Ranked.All(evaluation => evaluation.Decision == SelectionDecision.ManualReview));
            var reasons = result.Ranked.ToDictionary(evaluation => evaluation.Candidate.Languages.SingleOrDefault() ?? "", evaluation => evaluation.Reasons[^1]);
            Assert.AreEqual(("LanguageNotAllowed", "PL"), (reasons["PL"].Code, reasons["PL"].Value));
            Assert.AreEqual(("LanguageNotAllowed", "ZH"), (reasons["ZH"].Code, reasons["ZH"].Value));
            Assert.AreEqual(("LanguageNotAllowed", "EN"), (reasons["EN"].Code, reasons["EN"].Value));
        }

        [TestMethod]
        public void UnknownLanguage_IsTheLastResort_OnlyWhenNothingInTheChainIsFound_AndNeverForAStrictProfile()
        {
            var unknown = Candidate("Sun-mi Hwang - Almond (epub)", priority: 1, connection: "OPDS");

            // Alone, or next to files in other known languages (which stay manual), it is taken and says why.
            foreach (var fallback in new[] { LanguageFallbackType.Any, LanguageFallbackType.Original, LanguageFallbackType.English })
            {
                var others = NoGermanEdition().Where(candidate => candidate.Languages.Count > 0 && !candidate.Languages.Contains("EN"));
                var result = ReleaseSelectionEngine.Select(Profile(fallback), [unknown, .. others]);
                Assert.AreEqual(unknown.Id, result.Winner!.Candidate.Id, fallback.ToString());
                Assert.AreEqual("LanguageUnknownLastResort", result.WinnerReason!.Code);
                Assert.IsTrue(result.Ranked.Where(row => row.Candidate.Id != unknown.Id).All(row => row.Decision == SelectionDecision.ManualReview));
            }

            var strict = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.None), [unknown]);
            Assert.IsNull(strict.Winner);
            Assert.AreEqual("LanguageUnknown", strict.Ranked.Single().Reasons[^1].Code);

            // Any release in the chain wins over it, the original language included; then it is only a row for a person.
            var korean = Candidate("Sun-mi Hwang - Almond (KO) (epub)", priority: 50);
            var mixed = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Any), [unknown, korean]);
            Assert.AreEqual(korean.Id, mixed.Winner!.Candidate.Id);
            var row = mixed.Ranked.Single(item => item.Candidate.Id == unknown.Id);
            Assert.AreEqual(("LanguageUnknown", SelectionDecision.ManualReview), (row.Reasons[^1].Code, row.Decision));
        }

        [TestMethod]
        public void Flexible_TheAskedLanguageWinsOverEveryOtherAndOverUnknown_WhateverTheSourcePriority()
        {
            var german = Candidate("Sun-mi Hwang - Almond (DE) (epub)", priority: 90);
            var result = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Any), [.. NoGermanEdition(priority: 1), german]);

            Assert.AreEqual(german.Id, result.Winner!.Candidate.Id);
            Assert.AreEqual(1, result.Ranked.Count(evaluation => evaluation.Decision == SelectionDecision.Eligible));
            Assert.AreEqual(german.Id, result.Ranked[0].Candidate.Id);
        }

        [TestMethod]
        public void Flexible_TheOriginalLanguageIsAllowed_AfterTheAskedOne()
        {
            var korean = Candidate("Sun-mi Hwang - Almond (KO) (epub)");
            var result = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Any), [.. NoGermanEdition(), korean]);

            Assert.AreEqual(korean.Id, result.Winner!.Candidate.Id);
            Assert.AreEqual(1, result.Ranked.Count(evaluation => evaluation.Decision == SelectionDecision.Eligible));
        }

        [TestMethod]
        public void OriginalFallback_AllowsOnlyTheOriginal_EnglishFallback_OnlyEnglish_StrictNeither()
        {
            var offers = NoGermanEdition();
            Assert.IsNull(ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Original), offers.Where(candidate => candidate.Languages.Count > 0).ToArray()).Winner);
            Assert.AreEqual("Sun-mi Hwang - Almond (EN) (epub)", ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.English), offers).Winner!.Candidate.Parsed!.RawTitle);
            Assert.AreEqual("Sun-mi Hwang - Almond (EN) (epub)", ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Original, original: "EN"), offers).Winner!.Candidate.Parsed!.RawTitle);
            Assert.IsNull(ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.None, original: "EN"), offers).Winner);
        }

        [TestMethod]
        public void SeveralAskedLanguages_FollowTheOrderOfTheProfile()
        {
            var result = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.None, ["de", "en"]),
                [Candidate("Sun-mi Hwang - Almond (EN) (epub)"), Candidate("Sun-mi Hwang - Almond (DE) (epub)"), Candidate("Sun-mi Hwang - Almond (PL) (epub)")]);

            Assert.AreEqual("Sun-mi Hwang - Almond (DE) (epub)", result.Winner!.Candidate.Parsed!.RawTitle);
            Assert.AreEqual(2, result.Ranked.Count(evaluation => evaluation.Decision == SelectionDecision.Eligible));
        }

        [TestMethod]
        public void NoAskedLanguage_NoLanguageRule()
        {
            var result = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.Any, []), NoGermanEdition());

            Assert.IsNotNull(result.Winner);
        }

        [TestMethod]
        public void DirectSourceLanguages_AFileInSeveralLanguagesCountsForEach()
        {
            // Anna's Archive names every language of a file; the offer's name carries each of them before the format.
            var offer = new[] { "pl", "de, en", "xx", null }.Select(language => language).ToArray();
            var names = offer.Select(language => string.Concat(DirectSources.TrustedLanguageCodes(language).Select(code => " (" + code + ")")) + " (epub)").ToArray();
            CollectionAssert.AreEqual(new[] { " (PL) (epub)", " (DE) (EN) (epub)", " (epub)", " (epub)" }, names);

            var result = ReleaseSelectionEngine.Select(Profile(LanguageFallbackType.None), [Candidate("Sun-mi Hwang - Almond (DE) (EN) (epub)"), Candidate("Sun-mi Hwang - Almond (PL) (epub)")]);
            CollectionAssert.AreEquivalent(new[] { "DE", "EN" }, result.Winner!.Candidate.Languages.ToArray());
        }
    }
}
