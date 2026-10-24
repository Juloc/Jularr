using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Data.Sabnzbd;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadClientFileSafetyTests
    {
        private static SabnzbdConsoleSnapshot Snapshot(IReadOnlyList<SabnzbdQueueJob>? queue = null, IReadOnlyList<SabnzbdHistoryJob>? history = null, IReadOnlyList<string>? orphans = null)
            => new(queue ?? [], history ?? [], null, null, false, null, null, null, null, orphans ?? [], "/downloads/complete", "/downloads/incomplete");

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

                var preview = DownloadClientFileSafety.Preview("incomplete", root, "old", Snapshot(orphans: ["old"]));
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
        public void MountRelativePaths_NeverEscapeTheirSelectedStorageRoot()
        {
            Assert.AreEqual("Downloads/Complete", DownloadClientFileSafety.StorageRoot(
                "complete", "/data/complete", "Downloads/Complete", "/data/complete"));
            Assert.AreEqual("Movies", DownloadClientFileSafety.StorageRoot(
                "complete", "/data/complete/movies", "Movies", "/data/complete"));
            Assert.AreEqual("Downloads/Complete/category", DownloadClientFileSafety.StorageRoot(
                "complete", "/data/complete", "Downloads/Complete", "/data/complete/category"));
            Assert.AreEqual("Downloads/Incomplete", DownloadClientFileSafety.StorageRoot(
                "incomplete", "/data/incomplete", "Downloads/Incomplete", "/data/incomplete"));

            Assert.ThrowsExactly<DataValidationException>(() =>
                DownloadClientFileSafety.StorageRoot("complete", "/data/complete", "../out", "/data/complete"));
            Assert.ThrowsExactly<DataValidationException>(() =>
                DownloadClientFileSafety.StorageRoot("complete", "/data/complete", "/outside", "/data/complete"));
            Assert.ThrowsExactly<DataValidationException>(() =>
                DownloadClientFileSafety.StorageRoot("complete", "/data/complete", "Downloads", "/other"));
            Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.StorageRelative("../out"));
            Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.StorageRelative("other\\out"));
        }

        private sealed class SabnzbdHistoryDeleteCapture : HttpMessageHandler
        {
            public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                foreach (var pair in body.Split('&'))
                {
                    var parts = pair.Split('=', 2);
                    Fields[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : "");
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":true}""")
                };
            }
        }

        [TestMethod]
        public async Task TerminalSabnzbdHistoryCleanup_RemovesDataAndHistoryWithoutArchiving()
        {
            using var capture = new SabnzbdHistoryDeleteCapture();
            using var http = new HttpClient(capture);
            var client = new SabnzbdClient(http);
            await client.RemoveHistoryJobAsync(new SabnzbdConnection("https://sab.example.test", "secret-key"),
                "SABnzbd_nzo_123", CancellationToken.None);

            Assert.AreEqual("history", capture.Fields["mode"]);
            Assert.AreEqual("delete", capture.Fields["name"]);
            Assert.AreEqual("SABnzbd_nzo_123", capture.Fields["value"]);
            Assert.AreEqual("1", capture.Fields["del_files"]);
            Assert.AreEqual("0", capture.Fields["archive"]);
        }

        [TestMethod]
        public void AutomaticImportedSourceCleanup_DeletesOnlyMappedJobOutput()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-sab-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Downloaded.Job"));
                File.WriteAllText(Path.Combine(root, "Downloaded.Job", "movie.mkv"), "test");
                Directory.CreateDirectory(Path.Combine(root, "Keep.Job"));
                File.WriteAllText(Path.Combine(root, "Keep.Job", "other.mkv"), "keep");
                DownloadClientFileSafety.DeleteVerifiedCompleted(root, "Downloaded.Job");
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "Downloaded.Job")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "Keep.Job", "other.mkv")));
                Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.DeleteVerifiedCompleted(root, ""));
                Assert.ThrowsExactly<DataValidationException>(() => DownloadClientFileSafety.DeleteVerifiedCompleted(root, "../Keep.Job"));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [TestMethod]
        public void ManualDeletion_RequiresProviderReportedTopLevelIncompleteOrphan()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-sab-safety-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "unassigned"));
            Directory.CreateDirectory(Path.Combine(root, "orphan"));
            File.WriteAllText(Path.Combine(root, "orphan", "file.part"), "data");
            try
            {
                var snapshot = Snapshot(orphans: ["orphan"]);
                Assert.ThrowsExactly<DataValidationException>(() =>
                    DownloadClientFileSafety.Preview("complete", root, "orphan", snapshot));
                Assert.ThrowsExactly<DataValidationException>(() =>
                    DownloadClientFileSafety.Preview("incomplete", root, "unassigned", snapshot));
                Assert.ThrowsExactly<DataValidationException>(() =>
                    DownloadClientFileSafety.Preview("incomplete", root, "orphan/file.part", snapshot));
                var preview = DownloadClientFileSafety.Preview("incomplete", root, "orphan", snapshot);
                Assert.AreEqual(1, preview.Files);
                Assert.IsTrue(Directory.Exists(Path.Combine(root, "unassigned")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
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
