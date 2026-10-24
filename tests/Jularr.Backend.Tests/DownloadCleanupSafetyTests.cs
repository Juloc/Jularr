using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadCleanupSafetyTests
    {
        [TestMethod]
        public void AutomaticSourceCleanup_RequiresAllFiles()
        {
            Assert.IsTrue(ImportPlacementLogic.AllSourceFilesSelected(["episode.mkv"], ["episode.mkv"]));
            Assert.IsFalse(ImportPlacementLogic.AllSourceFilesSelected([], ["episode.mkv"]));
            Assert.IsFalse(ImportPlacementLogic.AllSourceFilesSelected(["episode.mkv"], []));
            Assert.IsFalse(ImportPlacementLogic.AllSourceFilesSelected([""], [""]));
            Assert.IsFalse(ImportPlacementLogic.AllSourceFilesSelected(["episode.mkv", "unselected.srt"], ["episode.mkv"]));
            Assert.IsFalse(ImportPlacementLogic.AllSourceFilesSelected(["episode.mkv", "notes/info.nfo"], ["episode.mkv"]));
        }
    }
}
