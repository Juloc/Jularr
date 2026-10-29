using System.Text.Json;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ReleaseGroupTests
    {
        private const string Title = "Mushoku Tensei: Jobless Reincarnation";
        private const string Good = "Mushoku Tensei - Jobless Reincarnation V06 EPUB [Seven Seas] [lucaz ] (EN)";
        private const string Bad = "Mushoku Tensei - Jobless Reincarnation V06 EPUB [Seven Seas] [Baka-Tsuki] (EN)";
        private const string Blocked = "Mushoku Tensei - Jobless Reincarnation V06 EPUB [Seven Seas] [DigitalMangaFan] (EN)";
        private const string Unrated = "Mushoku Tensei - Jobless Reincarnation V06 EPUB [Seven Seas] [Stick] (EN)";

        private static QualityProfile Profile() =>
            new("1", "Light novels", [.. BookQualities.Order], [.. BookQualities.Order], false, null, null, null)
            {
                IsBook = true,
                LanguageOrder = ["EN"],
                GroupRatings = new Dictionary<string, ReleaseGroupRating>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LuCaZ"] = ReleaseGroupRating.Good,
                    ["Baka-Tsuki"] = ReleaseGroupRating.Bad,
                    ["DigitalMangaFan"] = ReleaseGroupRating.Blocked
                }
            };

        private static SelectionResult Select(QualityProfile profile, bool automatic, params string[] titles)
        {
            var target = new WantedTargetData(1, 1, MediaType.Book, MediaSubtype.LightNovel, Title, null, [Title], 1, "Light novels", "{}", [], false, false, false, 0, [], ["en"], null, null, null);
            var offers = titles.Select(title => new ReleaseOffer(title, 1, "Nyaa", 25, title, new Uri("https://nyaa.example.test/download"), null, 10_000_000, null, ["title"])
                { Transport = DownloadTransport.Torrent });
            return AcquisitionSelectionLogic.Select(profile, target, AcquisitionKind.LightNovel, null, offers, automatic: automatic);
        }

        private static CandidateEvaluation Row(SelectionResult result, string title) => result.Ranked.Single(evaluation => evaluation.Candidate.Parsed?.RawTitle == title);

        [TestMethod]
        public void Score_RatedGroups_AddTheProfilesPointsAsANamedRule_InAnyCaseAndSpacing()
        {
            var result = Select(Profile(), true, Good, Bad, Unrated);
            var good = Row(result, Good);
            var bad = Row(result, Bad);
            var unrated = Row(result, Unrated);

            // The group's points are one more rule of the score; a group nobody rated adds nothing.
            Assert.AreEqual(unrated.PreferenceScore + 50, good.PreferenceScore);
            Assert.AreEqual(unrated.PreferenceScore - 50, bad.PreferenceScore);
            Assert.AreEqual(("GroupGood", "lucaz", 50), good.Score!.FormatPoints.Select(point => (point.Code, point.Value, point.Points)).Single());
            Assert.AreEqual(("GroupBad", "Baka-Tsuki", -50), bad.Score!.FormatPoints.Select(point => (point.Code, point.Value, point.Points)).Single());
            Assert.AreEqual(0, unrated.Score!.FormatPoints.Count);

            // A bad group lowers the score but is not rejected; the good one wins and an eligible row names its rating.
            Assert.AreEqual(SelectionDecision.Eligible, bad.Decision);
            Assert.AreEqual("GroupRatedBad", bad.Reasons[^1].Code);
            Assert.AreSame(good, result.Winner);
        }

        [TestMethod]
        public void Score_ProfilePoints_ReplaceTheDefaults()
        {
            var profile = Profile();
            profile = new QualityProfile(profile.Id, profile.Name, profile.AllowedQualities, profile.QualityOrder, false, null, null, null)
                { IsBook = true, LanguageOrder = ["EN"], GroupRatings = profile.GroupRatings, GoodGroupScore = 7, BadGroupScore = -3 };
            var result = Select(profile, true, Good, Bad, Unrated);

            Assert.AreEqual(Row(result, Unrated).PreferenceScore + 7, Row(result, Good).PreferenceScore);
            Assert.AreEqual(Row(result, Unrated).PreferenceScore - 3, Row(result, Bad).PreferenceScore);
        }

        [TestMethod]
        public void Selection_BlockedGroup_IsRejectedAutomatically_AndOnlyMarkedInAManualSearch()
        {
            var automatic = Row(Select(Profile(), true, Blocked), Blocked);
            Assert.AreEqual(SelectionDecision.Rejected, automatic.Decision);
            Assert.AreEqual("GroupBlocked", automatic.Reasons[^1].Code);

            var manual = Select(Profile(), false, Blocked);
            Assert.AreEqual(SelectionDecision.ManualReview, Row(manual, Blocked).Decision);
            Assert.AreEqual("GroupBlocked", Row(manual, Blocked).Reasons[^1].Code);
            Assert.IsNull(manual.Winner);
        }

        [TestMethod]
        public void ReadProfile_TakesTheRatingsOfTheMediaType_AndDefaultPointsOfAnOlderDefinition()
        {
            var definition = JsonSerializer.Serialize(new { AllowedQualities = new[] { "EPUB" }, QualityOrder = new[] { "EPUB" }, UpgradeAllowed = false });
            var target = new WantedTargetData(1, 1, MediaType.Book, null, Title, null, [Title], 1, "Books", definition, [], false, false, false, 0, [], ["en"], null, null, null,
                releaseGroupRatings: """{"LuCaZ":1,"DigitalMangaFan":3}""");
            var profile = AcquisitionSelectionLogic.ReadProfile(target);

            Assert.AreEqual(50, profile.GoodGroupScore);
            Assert.AreEqual(-50, profile.BadGroupScore);
            Assert.AreEqual(ReleaseGroupRating.Good, profile.GroupRatings["lucaz"]);
            Assert.AreEqual(ReleaseGroupRating.Blocked, ReleaseScorer.GroupRating(profile, BookReleaseJudge.Parse(Blocked)));
            Assert.IsNull(ReleaseScorer.GroupRating(profile, BookReleaseJudge.Parse(Unrated)));
        }

        [TestMethod]
        public void Sightings_TrimAndKeepEachGroupOnceInAnyCase_WithinTheStoredLength()
        {
            var names = ReleaseGroupLogic.Sightings([" GRP ", "grp", null, "", "   ", new string('x', 65), "LuCaZ", .. Enumerable.Range(0, 300).Select(index => "g" + index)]);

            CollectionAssert.AreEqual(new[] { "GRP", "LuCaZ" }, names.Take(2).ToArray());
            Assert.AreEqual(ReleaseGroupLogic.SightingLimit, names.Length);
            Assert.IsFalse(names.Any(name => name.Length > 64));
        }

        [TestMethod]
        public void Parse_BookAndMangaNames_StateTheirGroup()
        {
            Assert.AreEqual("Baka-Tsuki", BookReleaseJudge.Parse(Bad).ReleaseGroup);
            Assert.AreEqual("DigitalMangaFan", MangaReleaseJudge.Parse("Mushoku Tensei - Jobless Reincarnation v01 (2015) (Digital) (DigitalMangaFan)").ReleaseGroup);
            Assert.IsNull(BookReleaseJudge.Parse("Mushoku Tensei - Jobless Reincarnation V06 EPUB").ReleaseGroup);
        }
    }
}
