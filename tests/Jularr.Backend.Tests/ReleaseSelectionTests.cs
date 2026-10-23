using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ReleaseSelectionTests
    {
        // A request that asks for German, while the work was written in English.
        private static QualityProfile Profile() =>
            new("profile", "Profile", ["EPUB"], ["EPUB"], false, null, null, null)
            {
                IsBook = true,
                LanguageOrder = ["DE"],
                OriginalLanguage = "EN"
            };

        // The same request, but the profile names the fallback itself.
        private static QualityProfile ProfileWithFallback(LanguageFallbackType fallback, string original = "EN") =>
            new("profile", "Profile", ["EPUB"], ["EPUB"], false, null, null, null)
            {
                IsBook = true,
                LanguageOrder = ["DE"],
                OriginalLanguage = original,
                LanguageFallback = fallback
            };

        private static SelectionCandidate Candidate(string title, bool isPart = false, int priority = 0, string connection = "Anna")
        {
            var offer = new ReleaseOffer(title, 1, connection, priority, title, new Uri("https://example.org/file"), null, 1_000_000, null, ["title"]) { IsPart = isPart };
            var judgement = BookReleaseJudge.Judge(["Ikigai"], "García", offer, "EN");
            return new SelectionCandidate(offer.Identity, judgement.Parsed, offer.SizeBytes, offer.ConnectionName, offer.ConnectionPriority, offer.PublishedAt,
                judgement.Evidence, judgement.Coverage)
            {
                IndexerId = offer.ConnectionId,
                Languages = judgement.Languages ?? [],
                IsPart = offer.IsPart
            };
        }

        [TestMethod]
        public void Selection_RequestedLanguage_Comes_First()
        {
            var result = ReleaseSelectionEngine.Select(Profile(),
                [Candidate("Héctor García - IKIGAI (ES) (epub)"), Candidate("Héctor García - IKIGAI (DE) (epub)")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (DE) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_OriginalLanguage_Comes_Before_Any_Other()
        {
            // Nothing is in the asked language; the original language of the work wins over another one.
            var result = ReleaseSelectionEngine.Select(Profile(),
                [Candidate("Héctor García - IKIGAI (ES) (epub)"), Candidate("Héctor García - IKIGAI (EN) (epub)")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (EN) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_TheFallbackOfTheProfile_Comes_Before_Any_Other()
        {
            // Nothing is in the asked language; the profile names English as the fallback, so English wins over the Spanish original.
            var result = ReleaseSelectionEngine.Select(ProfileWithFallback(LanguageFallbackType.English, "ES"),
                [Candidate("Héctor García - IKIGAI (ES) (epub)"), Candidate("Héctor García - IKIGAI (EN) (epub)")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (EN) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_NoFallback_LeavesOtherLanguagesForManualReview()
        {
            var result = ReleaseSelectionEngine.Select(ProfileWithFallback(LanguageFallbackType.None, "IT"),
                [Candidate("Héctor García - IKIGAI (ES) (epub)"), Candidate("Héctor García - IKIGAI (IT) (epub)")]);

            Assert.IsNull(result.Winner);
            Assert.IsTrue(result.Ranked.All(evaluation => evaluation.Decision == SelectionDecision.ManualReview));
            Assert.IsTrue(result.Ranked.All(evaluation => evaluation.Reasons.Any(reason => reason.Code == "LanguageNotAllowed")));
        }

        [TestMethod]
        public void Selection_TheOriginalLanguage_Comes_Before_Any_Other_By_Default()
        {
            // The default fallback is the original language of the work.
            var result = ReleaseSelectionEngine.Select(ProfileWithFallback(LanguageFallbackType.Original, "IT"),
                [Candidate("Héctor García - IKIGAI (ES) (epub)"), Candidate("Héctor García - IKIGAI (IT) (epub)")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (IT) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_TheAskedLanguage_Comes_Before_TheFallback()
        {
            // A file in an asked language always wins over the fallback.
            var result = ReleaseSelectionEngine.Select(ProfileWithFallback(LanguageFallbackType.English, "ES"),
                [Candidate("Héctor García - IKIGAI (EN) (epub)"), Candidate("Héctor García - IKIGAI (DE) (epub)")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (DE) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_A_Part_Comes_Last()
        {
            var result = ReleaseSelectionEngine.Select(Profile(),
                [Candidate("Héctor García - IKIGAI (EN) (epub)", true), Candidate("Héctor García - IKIGAI (EN) (epub)")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (EN) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_ACompleteFile_Comes_Before_A_Part_In_The_Asked_Language()
        {
            // A part is only a part, even in the language the request asks for.
            var result = ReleaseSelectionEngine.Select(Profile(),
                [Candidate("Héctor García - IKIGAI (DE) (epub)", true), Candidate("Héctor García - IKIGAI (EN) (epub)")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (EN) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_TheSourceWithTheHigherPriority_Comes_First()
        {
            // As in Readarr: the preferred source decides, the quality only among its own releases.
            var result = ReleaseSelectionEngine.Select(Profile(),
                [Candidate("Héctor García - IKIGAI (EN) (epub)", false, 2, "Usenet"), Candidate("Héctor García - IKIGAI (DE) (epub)", false, 1, "Anna")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (DE) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
            Assert.AreEqual("IndexerPriority", result.WinnerReason!.Code);
        }

        [TestMethod]
        public void Selection_RequestedLanguageBeatsHigherPriorityFallback()
        {
            var result = ReleaseSelectionEngine.Select(Profile(),
                [Candidate("Héctor García - IKIGAI (EN) (epub)", false, 1, "Usenet"),
                 Candidate("Héctor García - IKIGAI (DE) (epub)", false, 50, "Anna")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (DE) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Selection_UnrequestedLanguageIsManualEvenWhenItIsTheOnlyResult()
        {
            var result = ReleaseSelectionEngine.Select(ProfileWithFallback(LanguageFallbackType.Original, "JA"),
                [Candidate("Héctor García - IKIGAI (ES) (epub)")]);

            Assert.IsNull(result.Winner);
            Assert.AreEqual(SelectionDecision.ManualReview, result.Ranked.Single().Decision);
            Assert.AreEqual("LanguageNotAllowed", result.Ranked.Single().Reasons.Last().Code);
        }

        [TestMethod]
        public void Selection_WithinOneSource_TheQuality_Still_Decides()
        {
            var result = ReleaseSelectionEngine.Select(Profile(),
                [Candidate("Héctor García - IKIGAI (ES) (epub)", false, 1, "Anna"), Candidate("Héctor García - IKIGAI (EN) (epub)", false, 1, "Anna")]);

            Assert.IsNotNull(result.Winner);
            Assert.AreEqual("Héctor García - IKIGAI (EN) (epub)", result.Winner.Candidate.Parsed!.RawTitle);
        }
    }
}
