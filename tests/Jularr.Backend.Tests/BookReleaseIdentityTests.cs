using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class BookReleaseIdentityTests
    {
        private const string Original = "The 48 Laws of Power";
        private const string ObservedGuide = "Greene, Robert - The expert guide to Robert Greene's The 48 laws of power -- in 30 minutes (EN) (epub)";

        [TestMethod]
        [DataRow("title")]
        [DataRow("id")]
        public void ObservedDerivative_IsNotTheOriginalEvenWithAuthorOrIdentifierSearch(string stage)
        {
            Assert.AreEqual(BookIdentityMatch.WrongTitle, Judge([Original], ObservedGuide, stage: stage).Match);
        }

        [TestMethod]
        [DataRow("Summary of The 48 Laws of Power")]
        [DataRow("The 48 Laws of Power Study Guide")]
        [DataRow("The 48 Laws of Power Workbook")]
        [DataRow("The 48 Laws of Power - Kurzfassung")]
        [DataRow("Zusammenfassung: The 48 Laws of Power")]
        [DataRow("The 48 Laws of Power (abridged)")]
        [DataRow("The 48 Laws of Power - condensed edition")]
        [DataRow("A guide to The 48 Laws of Power")]
        public void DerivativeOfRequestedOriginal_IsRejected(string derivative)
        {
            Assert.AreEqual(BookIdentityMatch.WrongTitle, Judge([Original], "Robert Greene - " + derivative + " (EN) (epub)").Match);
        }

        [TestMethod]
        public void UnnamedAuthor_DoesNotMakeASummaryEligible()
        {
            Assert.AreEqual(BookIdentityMatch.WrongTitle, Judge([Original], "Summary of The 48 Laws of Power (EN) (epub)", author: null).Match);
        }

        [TestMethod]
        [DataRow("The 48 Laws of Power")]
        [DataRow("The 48 Laws of Power: Illustrated Edition")]
        [DataRow("The 48 Laws of Power: A Guide to Putting the Laws into Practice")]
        [DataRow("The 48 Laws of Power (unabridged)")]
        public void OriginalAndOrdinarySubtitleOrEdition_RemainEligible(string release)
        {
            Assert.AreEqual(BookIdentityMatch.Matches, Judge([Original], "Robert Greene - " + release + " (EN) (epub)").Match);
        }

        [TestMethod]
        [DataRow("Summary of The 48 Laws of Power")]
        [DataRow("The Expert Guide to The 48 Laws of Power")]
        [DataRow("A Guide to The 48 Laws of Power")]
        [DataRow("The 48 Laws of Power Study Guide")]
        public void ExplicitlyRequestedDerivative_IsEligibleForItself(string requested)
        {
            Assert.AreEqual(BookIdentityMatch.Matches, Judge([requested], "Robert Greene - " + requested + " (EN) (epub)").Match);
        }

        [TestMethod]
        public void RequestedSummaryAlias_IsRespectedButDoesNotPermitAnotherDerivative()
        {
            Assert.AreEqual(BookIdentityMatch.Matches,
                Judge([Original, "Summary of The 48 Laws of Power"], "Robert Greene - Summary of The 48 Laws of Power (EN) (epub)").Match);
            Assert.AreEqual(BookIdentityMatch.WrongTitle,
                Judge([Original, "Summary of The 48 Laws of Power"], "Robert Greene - The 48 Laws of Power Study Guide (EN) (epub)").Match);
        }

        [TestMethod]
        public void AuthorNamedGuide_IsNotADerivativeMarker()
        {
            Assert.AreEqual(BookIdentityMatch.Matches, Judge([Original], "Guide, Robert - The 48 Laws of Power (EN) (epub)", author: "Robert Guide").Match);
        }

        [TestMethod]
        public void GuideAboutAnExplicitlyRequestedGuide_IsStillAnotherWork()
        {
            Assert.AreEqual(BookIdentityMatch.WrongTitle, Judge(["A Guide to The 48 Laws of Power"],
                "Robert Greene - A Guide to A Guide to The 48 Laws of Power (EN) (epub)").Match);
        }

        private static ReleaseJudgement<BookIdentityMatch> Judge(string[] titles, string release, string? author = "Robert Greene", string stage = "title") =>
            BookReleaseJudge.Judge(titles, author, new ReleaseOffer(release, 1, "Anna", 50, release,
                new Uri("https://annas.example.test/md5/0123456789abcdef0123456789abcdef"), null, 1_000_000, null, [stage]));
    }
}
