using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Tests
{
    // How good a stored file is: its quality against the cutoff of the profile and its size against the profile's range for that quality.
    [TestClass]
    public sealed class WorkGradeLogicTests
    {
        private static QualityProfile Profile() => new("1", "HD", ["WEBDL-1080p", "HDTV-720p"], ["WEBDL-1080p", "HDTV-720p", "SDTV"], true, "WEBDL-1080p", null, null)
        {
            Sizes = new Dictionary<string, QualitySize> { ["WEBDL-1080p"] = new("WEBDL-1080p", 10m, 30m, 80m) },
            RuntimeMinutes = 24
        };

        [TestMethod]
        public void Quality_IsMetAtTheCutoff_UpgradeBelowItAndNotAllowedOutsideTheProfile()
        {
            var profile = Profile();
            Assert.AreEqual(FileQualityGrade.Met, WorkGradeLogic.Quality(profile, "WEBDL-1080p"));
            Assert.AreEqual(FileQualityGrade.Upgrade, WorkGradeLogic.Quality(profile, "HDTV-720p"));
            Assert.AreEqual(FileQualityGrade.NotAllowed, WorkGradeLogic.Quality(profile, "SDTV"));
            Assert.AreEqual(FileQualityGrade.Unknown, WorkGradeLogic.Quality(profile, null));
            Assert.AreEqual(FileQualityGrade.Unknown, WorkGradeLogic.Quality(null, "WEBDL-1080p"));
        }

        [TestMethod]
        public void Size_IsGoodInRange_SmallBelowIt_AndALeanCodecMayBeMuchSmaller()
        {
            var profile = Profile();

            // 24 minutes: 1.0 GB is 41.7 MB per minute, within 10 to 80.
            var (good, perMinute) = WorkGradeLogic.Size(profile, "WEBDL-1080p", 1_000_000_000, 24 * 60_000, "h264");
            Assert.AreEqual(FileSizeGrade.Good, good);
            Assert.AreEqual(41.7m, perMinute);

            // 0.2 GB is 8.3: small for x264, fine for HEVC, which may go down to half the minimum.
            Assert.AreEqual(FileSizeGrade.Small, WorkGradeLogic.Size(profile, "WEBDL-1080p", 200_000_000, 24 * 60_000, "h264").Grade);
            Assert.AreEqual(FileSizeGrade.Good, WorkGradeLogic.Size(profile, "WEBDL-1080p", 200_000_000, 24 * 60_000, "hevc").Grade);
            Assert.AreEqual(FileSizeGrade.Small, WorkGradeLogic.Size(profile, "WEBDL-1080p", 100_000_000, 24 * 60_000, "hevc").Grade);

            // 3 GB is 125 MB per minute, above the maximum.
            Assert.AreEqual(FileSizeGrade.Large, WorkGradeLogic.Size(profile, "WEBDL-1080p", 3_000_000_000, 24 * 60_000, "h264").Grade);

            // Without a probe the profile's runtime counts; without any length or limit there is no grade.
            Assert.AreEqual(FileSizeGrade.Good, WorkGradeLogic.Size(profile, "WEBDL-1080p", 1_000_000_000, null, null).Grade);
            Assert.AreEqual(FileSizeGrade.Unknown, WorkGradeLogic.Size(profile, "SDTV", 1_000_000_000, null, null).Grade);
            Assert.AreEqual(FileSizeGrade.Unknown, WorkGradeLogic.Size(null, "WEBDL-1080p", 1_000_000_000, null, null).Grade);
        }
    }
}
