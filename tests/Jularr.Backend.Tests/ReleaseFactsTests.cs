using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ReleaseFactsTests
    {
        [TestMethod]
        public void ReleaseFacts_NameOrFolder_TellGroupQualityEditionAndRevision()
        {
            // A scene folder says more than a plain file name inside it.
            var folder = ReleaseFacts.OfPath("Some.Movie.2019.Directors.Cut.PROPER.2160p.BluRay.x265-TEAM/movie.mkv")!;
            Assert.AreEqual("TEAM", folder.ReleaseGroup);
            Assert.AreEqual("BLURAY-2160p", folder.QualityKey);
            Assert.AreEqual("Directors Cut", folder.Edition);
            Assert.AreEqual(2, folder.Revision);

            // A named file keeps its own facts.
            var file = ReleaseFacts.OfPath("Show/Season 01/Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv")!;
            Assert.AreEqual("GRP", file.ReleaseGroup);
            Assert.AreEqual("WEB-1080p", file.QualityKey);
            Assert.IsNull(file.Edition);
            Assert.AreEqual(1, file.Revision);

            // Title words are no edition or group; Jularr's own tag is the quality; plain names say nothing.
            Assert.IsNull(ReleaseFacts.Of("Uncut.Gems.2019.1080p.WEB-DL-GRP")!.Edition);
            Assert.IsNull(ReleaseFacts.Of("Spider-Man.mkv"));
            Assert.AreEqual("WEB-1080p", ReleaseFacts.Of("Example Movie (2020) [WEB-1080p].mkv")!.QualityKey);
            Assert.IsNull(ReleaseFacts.OfPath("Show/Season 01/cover.jpg"));
        }
    }
}
