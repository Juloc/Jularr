using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Sabnzbd;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadClientFileSafetyTests
    {
        private static SabnzbdConsoleSnapshot Snapshot(IReadOnlyList<SabnzbdQueueJob>? queue = null, IReadOnlyList<SabnzbdHistoryJob>? history = null)
            => new(queue ?? [], history ?? [], null, null, false, null, null, null, null, [], "/downloads/complete", "/downloads/incomplete");

        [TestMethod]
        public void NestedFolder_ListsAllEntriesAcrossPages_AndDeletesOnlyConfirmedSelection()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-sab-console-" + Guid.NewGuid().ToString("N"));
            var child = Path.Combine(root, "old");
            Directory.CreateDirectory(child);
            try
            {
                for (var i = 0; i < 205; i++) File.WriteAllText(Path.Combine(child, $"file-{i:D4}.txt"), "test");
                var first = DownloadClientFileSafety.Browse("complete", root, "old", 1, Snapshot());
                var second = DownloadClientFileSafety.Browse("complete", root, "old", 2, Snapshot());
                Assert.AreEqual(200, first.Entries.Count);
                Assert.IsTrue(first.HasMore);
                Assert.AreEqual(5, second.Entries.Count);
                Assert.IsFalse(second.HasMore);

                var preview = DownloadClientFileSafety.Preview("complete", root, "old", Snapshot());
                Assert.AreEqual(205, preview.Files);
                Assert.AreEqual(4 * 205L, preview.Bytes);
                DownloadClientFileSafety.Delete(root, "old");
                Assert.IsFalse(Directory.Exists(child));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [TestMethod]
        public void KnownQueueAndHistoryFolders_CannotBeDeleted()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-sab-console-" + Guid.NewGuid().ToString("N"));
            var active = Path.Combine(root, "Active.Release");
            var finished = Path.Combine(root, "Finished.Release");
            Directory.CreateDirectory(active);
            Directory.CreateDirectory(finished);
            try
            {
                var snapshot = Snapshot([new SabnzbdQueueJob("SABnzbd_nzo_1", "Active.Release", 40)],
                    [new SabnzbdHistoryJob("SABnzbd_nzo_2", "Finished.Release", "Completed", finished, SabnzbdFailureKind.None)]);
                Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.Preview("incomplete", root, "Active.Release", snapshot));
                Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.Preview("complete", root, "Finished.Release", snapshot));
                Assert.IsTrue(Directory.Exists(active));
                Assert.IsTrue(Directory.Exists(finished));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [TestMethod]
        public void TraversalAndRootDeletion_AreRejected()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-sab-console-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.SafePath(root, "../outside"));
                Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.SafePath(root, ""));
                Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.Preview("complete", root, "../outside", Snapshot()));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
