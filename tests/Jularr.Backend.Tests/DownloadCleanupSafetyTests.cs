using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Storage;

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
            var initial = DownloadSourceInventory.Fingerprint([new StorageFile("episode.mkv", 100, DateTime.UnixEpoch)]);
            Assert.AreEqual(initial, DownloadSourceInventory.Fingerprint([new StorageFile("episode.mkv", 100, DateTime.UnixEpoch)]));
            Assert.AreNotEqual(initial, DownloadSourceInventory.Fingerprint([new StorageFile("episode.mkv", 101, DateTime.UnixEpoch)]));
            Assert.AreNotEqual(initial, DownloadSourceInventory.Fingerprint([
                new StorageFile("episode.mkv", 100, DateTime.UnixEpoch),
                new StorageFile("new.srt", 10, DateTime.UnixEpoch)]));
        }
    }
}
