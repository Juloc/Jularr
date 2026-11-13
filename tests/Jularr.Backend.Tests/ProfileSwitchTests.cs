using System.Text.Json;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    // The simple switches of a video profile in a search of a film: bad sources, x265 and the file size.
    [TestClass]
    public sealed class ProfileSwitchTests
    {
        private const string Plain = "Example.Movie.2020.1080p.WEB-DL.H264-AAA";
        private const string Hevc = "Example.Movie.2020.1080p.WEB-DL.x265-BBB";
        private const string Cinema = "Example.Movie.2020.1080p.HDCAM.H264-CCC";
        private const string ThreeD = "Example.Movie.2020.3D.1080p.WEB-DL.H264-DDD";

        private static SelectionResult Search(AcquisitionProfileDefinition definition, bool automatic, params (string Title, long Size)[] offers)
        {
            var target = new WantedTargetData(1, 1, MediaType.Movie, null, "Example Movie", 2020, ["Example Movie"], 1, "Movies", JsonSerializer.Serialize(definition, AcquisitionSelectionLogic.DefinitionOptions),
                [], false, false, false, 0, [], [], null, null, null);
            var releases = offers.Select(offer => new ReleaseOffer(offer.Title, 1, "Indexer", 0, offer.Title, new Uri("https://example.org/" + Uri.EscapeDataString(offer.Title)), null, offer.Size, null, ["title"]));
            return AcquisitionSelectionLogic.Select(AcquisitionSelectionLogic.ReadProfile(target), target, AcquisitionKind.Movie, null, releases, automatic: automatic);
        }

        private static AcquisitionProfileDefinition Profile(bool blockBadSources = true, CodecPreference codec = CodecPreference.Any, SizePreference size = SizePreference.Any) =>
            new(["WEBDL-1080p", "CAM"], ["WEBDL-1080p", "CAM"], false, null, null, null) { BlockBadSources = blockBadSources, CodecPreference = codec, SizePreference = size };

        private static CandidateEvaluation Row(SelectionResult result, string title) => result.Ranked.Single(evaluation => evaluation.Candidate.Parsed?.RawTitle == title);

        [TestMethod]
        public void BadSources_AreNeverTaken_UnlessAllowed()
        {
            // A cinema recording and a 3D release are rejected even when their quality is allowed; allowed again they compete like any other.
            var blocked = Search(Profile(), true, (Cinema, 2_000_000_000), (ThreeD, 4_000_000_000), (Plain, 4_000_000_000));
            Assert.AreEqual(SelectionDecision.Rejected, Row(blocked, Cinema).Decision);
            Assert.AreEqual("CAM", Row(blocked, Cinema).Score!.RejectionReasons.Single(reason => reason.Code == "BadSource").Value);
            Assert.AreEqual("3D", Row(blocked, ThreeD).Score!.RejectionReasons.Single(reason => reason.Code == "BadSource").Value);
            Assert.AreEqual(SelectionDecision.Rejected, Row(blocked, ThreeD).Decision);
            Assert.AreEqual(Plain, blocked.Winner!.Candidate.Parsed!.RawTitle);

            var allowed = Search(Profile(blockBadSources: false), true, (Cinema, 2_000_000_000), (ThreeD, 4_000_000_000));
            Assert.IsFalse(allowed.Ranked.Any(row => row.Score!.RejectionReasons.Any(reason => reason.Code == "BadSource")));
        }

        [TestMethod]
        public void Codec_LikedOrAvoided_AddsOrTakesItsPoints()
        {
            var liked = Search(Profile(codec: CodecPreference.PreferHevc), true, (Plain, 4_000_000_000), (Hevc, 4_000_000_000));
            Assert.AreEqual(("CodecLiked", "x265", ReleaseScorer.CodecPoints), Row(liked, Hevc).Score!.FormatPoints.Select(point => (point.Code, point.Value, point.Points)).Single());
            Assert.AreEqual(Hevc, liked.Winner!.Candidate.Parsed!.RawTitle);

            var avoided = Search(Profile(codec: CodecPreference.AvoidHevc), true, (Plain, 4_000_000_000), (Hevc, 4_000_000_000));
            Assert.AreEqual(-ReleaseScorer.CodecPoints, Row(avoided, Hevc).Score!.FormatScore);
            Assert.AreEqual(Plain, avoided.Winner!.Candidate.Parsed!.RawTitle);

            var alike = Search(Profile(), true, (Plain, 4_000_000_000), (Hevc, 4_000_000_000));
            Assert.AreEqual(0, Row(alike, Hevc).Score!.FormatPoints.Count);
        }

        [TestMethod]
        public void Size_DecidesBetweenReleasesOfTheSameQualityAndPoints()
        {
            const string Large = "Example.Movie.2020.1080p.WEB-DL.H264-LRG";
            const string Small = "Example.Movie.2020.1080p.WEB-DL.H264-SML";
            Assert.AreEqual(Small, Search(Profile(size: SizePreference.Smaller), true, (Large, 8_000_000_000), (Small, 2_000_000_000)).Winner!.Candidate.Parsed!.RawTitle);
            Assert.AreEqual(Large, Search(Profile(size: SizePreference.Larger), true, (Small, 2_000_000_000), (Large, 8_000_000_000)).Winner!.Candidate.Parsed!.RawTitle);
        }

        [TestMethod]
        public void Definition_WithoutSwitches_BlocksBadSourcesAndIsAlikeOtherwise()
        {
            var stored = JsonSerializer.Deserialize<AcquisitionProfileDefinition>("""{"AllowedQualities":["WEBDL-1080p"],"QualityOrder":["WEBDL-1080p"],"UpgradeAllowed":false}""",
                AcquisitionSelectionLogic.DefinitionOptions)!;
            Assert.AreEqual((true, CodecPreference.Any, SizePreference.Any), (stored.BlockBadSources, stored.CodecPreference, stored.SizePreference));
        }
    }
}
