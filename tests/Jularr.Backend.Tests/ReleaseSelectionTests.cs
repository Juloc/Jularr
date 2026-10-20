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

        private static SelectionCandidate Candidate(string title, bool isPart = false)
        {
            var offer = new ReleaseOffer(title, 1, "Anna", 0, title, new Uri("https://example.org/file"), null, 1_000_000, null, ["title"]) { IsPart = isPart };
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
    }
}
