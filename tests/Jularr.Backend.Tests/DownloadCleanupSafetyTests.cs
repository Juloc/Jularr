using Jularr.Backend.Service.System.Acquisition.V1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadCleanupSafetyTests
    {
        [TestMethod]
        public void AutomaticSourceCleanup_RequiresAllFiles()
        {
            Assert.IsTrue(DownloadImport.AllFilesCommitted(["episode.mkv"], ["episode.mkv"]));
            Assert.IsFalse(DownloadImport.AllFilesCommitted(["episode.mkv", "unselected.srt"], ["episode.mkv"]));
            Assert.IsFalse(DownloadImport.AllFilesCommitted(["episode.mkv", "notes/info.nfo"], ["episode.mkv"]));
        }
    }
}
