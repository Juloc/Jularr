using System.Text.Json;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SearchReassessmentTests
    {
        private static QualityProfile Profile(bool upgrades = true) => new("test", "Current", ["WEBDL-1080p"], ["Bluray-1080p", "WEBDL-1080p"], upgrades, "WEBDL-1080p", null, null)
        {
            RuntimeMinutes = 60,
            Sizes = new Dictionary<string, QualitySize> { ["WEBDL-1080p"] = new("WEBDL-1080p", 1, 20, 30) }
        };

        private static WantedTargetData Target(params (long? Unit, string Quality, string Name, long Bytes)[] files) =>
            new(1, 1, MediaType.Series, null, "Example", 2020, [], 1, "Current", null, [], false, true, false, 0, [], [], null, null, null,
                installedFiles: JsonSerializer.Serialize(files.Select(file => new { WorkUnitId = file.Unit, file.Quality,
                    Facts = new StoredReleaseEvidence(file.Name, file.Bytes, [], false, "en", 2020, MediaType.Series) })));

        [TestMethod]
        [DataRow("Bluray-1080p", "Example.S01E01.1080p.BluRay.x264-GRP", 1000L)]
        [DataRow("WEBDL-1080p", "Example.S01E01.1080p.WEB-DL.x264-GRP", 3000L)]
        public void CurrentRules_RejectedInstalledFile_CanBeReplacedByAnAllowedRelease(string quality, string title, long mebibytes)
        {
            var profile = Profile();
            var installed = AcquisitionSelectionLogic.Installed(profile, Target((null, quality, title, mebibytes * 1_048_576)))!.Value;
            Assert.IsTrue(installed.NeedsReplacement);
            ReleaseParser.TryParse("Example.S01E01.1080p.WEB-DL.x264-GRP", out var parsed);
            var candidate = new SelectionCandidate("replacement", parsed, 1000L * 1_048_576, "source", 1, null, ReleaseIdentityEvidence.Exact("Exact"), new SelectionCoverage(1, 1, 0))
            {
                InstalledQuality = installed.Quality,
                InstalledFormatScore = installed.FormatScore,
                InstalledNeedsReplacement = installed.NeedsReplacement
            };
            Assert.IsNotNull(ReleaseSelectionEngine.Select(profile, [candidate]).Winner);
        }

        [TestMethod]
        public void CurrentRules_AValidExistingVersion_AndDisabledUpgrades_AreRetained()
        {
            var valid = AcquisitionSelectionLogic.Installed(Profile(), Target(
                (null, "Bluray-1080p", "Example.S01E01.1080p.BluRay.x264-GRP", 1000L * 1_048_576),
                (null, "WEBDL-1080p", "Example.S01E01.1080p.WEB-DL.x264-GRP", 1000L * 1_048_576)))!.Value;
            Assert.AreEqual("WEBDL-1080p", valid.Quality);
            Assert.IsFalse(AcquisitionSelectionLogic.NeedsUpgrade(Profile(), valid));
            var disabled = Profile(upgrades: false);
            var rejected = AcquisitionSelectionLogic.Installed(disabled, Target((null, "Bluray-1080p", "Example.S01E01.1080p.BluRay.x264-GRP", 1000L * 1_048_576)))!.Value;
            Assert.IsFalse(AcquisitionSelectionLogic.NeedsUpgrade(disabled, rejected));
        }

        [TestMethod]
        public void Units_ReassessEveryCoveredEpisode_WithoutSearchingTheValidOne()
        {
            var target = Target((11, "WEBDL-1080p", "Example.S01E01.1080p.WEB-DL.x264-GRP", 3000L * 1_048_576),
                (12, "WEBDL-1080p", "Example.S01E02.1080p.WEB-DL.x264-GRP", 1000L * 1_048_576));
            var units = AcquisitionSelectionLogic.ReassessUnits(Profile(), target, [new(11, 1, 1, true, true, false), new(12, 1, 2, true, true, false), new(13, 1, 3, true, false, false)]);
            CollectionAssert.AreEqual(new[] { true, false, true }, units.Select(unit => unit.NeedsSearch).ToArray());
        }

        [TestMethod]
        public void SeasonPack_KnownTotalRuntime_UsesTheSameSizeBounds()
        {
            ReleaseParser.TryParse("Example.S01.1080p.WEB-DL.x264-GRP", out var parsed);
            var accepted = ReleaseScorer.Score(Profile(), new ReleaseCandidate(parsed!, 1000L * 1_048_576) { RuntimeMinutes = 600 });
            var rejected = ReleaseScorer.Score(Profile(), new ReleaseCandidate(parsed!, 20_000L * 1_048_576) { RuntimeMinutes = 600 });
            Assert.IsTrue(accepted.Accepted);
            Assert.IsFalse(rejected.Accepted);
            Assert.IsTrue(rejected.RejectionReasons.Any(reason => reason.Code == "SizeAboveQualityMaximum"));
        }

        [TestMethod]
        public void ReusedEvidence_StillUsesTheCurrentFormatScoresAndUpgradeSwitch()
        {
            var target = Target((null, "WEBDL-1080p", "Example.S01E01.1080p.WEB-DL.x264-GRP", 1000L * 1_048_576));
            Assert.IsFalse(AcquisitionSelectionLogic.NeedsUpgrade(Profile(), AcquisitionSelectionLogic.Installed(Profile(), target)!.Value));
            var format = new CustomFormat(1, "Group", false, [new("Group", FormatConditionKind.ReleaseGroup, false, false) { Value = "GRP" }]);
            var changed = new QualityProfile("test", "Changed", ["WEBDL-1080p"], ["WEBDL-1080p"], true, "WEBDL-1080p", null, null)
            {
                MinimumFormatScore = 100,
                Formats = [(format, 25)],
                CutoffFormatScore = 100
            };
            var installed = AcquisitionSelectionLogic.Installed(changed, target)!.Value;
            Assert.AreEqual(25, installed.FormatScore);
            Assert.IsTrue(installed.NeedsReplacement, "Parsed evidence is reused, but a changed profile is always re-evaluated.");
            ReleaseParser.TryParse("Example.S01E01.1080p.WEB-DL.x264-GRP", out var parsed);
            var candidate = new SelectionCandidate("new", parsed, 1000L * 1_048_576, "source", 1, null, ReleaseIdentityEvidence.Exact("Exact"), new SelectionCoverage(1, 1, 0))
            {
                InstalledQuality = "WEBDL-1080p", InstalledNeedsReplacement = true
            };
            Assert.IsNull(ReleaseSelectionEngine.Select(Profile(upgrades: false), [candidate]).Winner);
        }
    }
}
