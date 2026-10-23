using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Logic.Usenet;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadStorageTests
    {
        private static DownloadStoragePaths Paths(string root, long? jobLimit = null, long? workspaceLimit = null)
        {
            var incomplete = Directory.CreateDirectory(Path.Combine(root, "Incomplete")).FullName;
            var complete = Directory.CreateDirectory(Path.Combine(root, "Complete")).FullName;
            return new DownloadStoragePaths(new DownloadStorageSettingsData(DownloadStorageArea.Direct, 1, "Incomplete", StorageFileSystem.DownloadFilesystemIdentity(incomplete),
                1, "Complete", StorageFileSystem.DownloadFilesystemIdentity(complete), 0, jobLimit, workspaceLimit, 2, 1, Guid.NewGuid()),
                Path.Combine(incomplete, "workspace"), Path.Combine(complete, "workspace"), new DownloadStorageReservations());
        }

        [TestMethod]
        public void ParallelJobs_EnforcesConfiguredLimitAndRejectsDuplicateExecution()
        {
            var reservations = new DownloadStorageReservations();
            var workspace = Guid.NewGuid();
            using var first = reservations.TryStart(workspace, 1, 2);
            using var second = reservations.TryStart(workspace, 2, 2);
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.IsNull(reservations.TryStart(workspace, 1, 2));
            Assert.IsNull(reservations.TryStart(workspace, 3, 2));
            first.Dispose();
            using var next = reservations.TryStart(workspace, 3, 2);
            Assert.IsNotNull(next);
            Assert.IsNull(reservations.TryStart(workspace, 4, 1), "Lowering the configured limit must block new starts.");
        }

        [TestMethod]
        public async Task WorkspaceLimit_IncludesConcurrentWritesAndHiddenWorkingFiles()
        {
            using var folder = new TemporaryDirectory();
            var paths = Paths(folder.Path, workspaceLimit: 3_000);
            var first = await paths.PrepareIncompleteAsync(1, CancellationToken.None);
            var second = await paths.PrepareIncompleteAsync(2, CancellationToken.None);
            using (paths.ReserveWrite(1, first, 2_000))
            {
                Assert.Throws<ServiceUnavailableException>(() => paths.ReserveWrite(2, second, 2_000));
            }

            File.WriteAllBytes(Path.Combine(first, ".partial"), new byte[2_000]);
            if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Combine(first, ".partial"), FileAttributes.Hidden);
            Assert.Throws<ServiceUnavailableException>(() => paths.ReserveWrite(2, second, 2_000));
            using var remaining = paths.ReserveWrite(2, second, 1_000);
        }

        [TestMethod]
        public async Task DirectWrite_StopsBeforeExceedingWorkspaceAndPreservesExistingJobs()
        {
            using var folder = new TemporaryDirectory();
            var paths = Paths(folder.Path, workspaceLimit: 100_000);
            var directory = await paths.PrepareIncompleteAsync(1, CancellationToken.None);
            var partial = Path.Combine(directory, "book.part");
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[200_000]) };
            await Assert.ThrowsAsync<ServiceUnavailableException>(() => DirectDownload.SaveAsync(response, partial, null, CancellationToken.None,
                bytes => paths.ReserveWrite(1, directory, bytes)));
            Assert.IsTrue(new FileInfo(partial).Length <= 100_000);
            Assert.IsTrue(new FileInfo(partial).Length > 0);
        }

        [TestMethod]
        public async Task Complete_OnSameFilesystemMovesPayloadWithoutDuplicatingBudget()
        {
            using var folder = new TemporaryDirectory();
            var paths = Paths(folder.Path, jobLimit: 1_000, workspaceLimit: 1_000);
            var directory = await paths.PrepareIncompleteAsync(1, CancellationToken.None);
            File.WriteAllBytes(Path.Combine(directory, "book.epub"), new byte[1_000]);
            var complete = await paths.CompleteAsync(1, directory, null, CancellationToken.None);
            Assert.IsFalse(Directory.Exists(directory));
            Assert.AreEqual(1_000L, new FileInfo(Path.Combine(complete, "book.epub")).Length);
            paths.CheckCapacity();
        }

        [TestMethod]
        public async Task ExpandedPayload_IsIndependentOfDownloadSizeLimitButCountsTowardWorkspace()
        {
            using var folder = new TemporaryDirectory();
            var paths = Paths(folder.Path, jobLimit: 1_000, workspaceLimit: 5_000);
            var incomplete = await paths.PrepareIncompleteAsync(1, CancellationToken.None);
            File.WriteAllBytes(Path.Combine(incomplete, "archive.zip"), new byte[1_000]);
            var output = Directory.CreateDirectory(Path.Combine(incomplete, "output")).FullName;
            File.WriteAllBytes(Path.Combine(output, "book.epub"), new byte[3_000]);
            var complete = await paths.CompleteAsync(1, output, null, CancellationToken.None);
            Assert.AreEqual(3_000L, new FileInfo(Path.Combine(complete, "book.epub")).Length);
            Assert.Throws<ServiceUnavailableException>(() => paths.CheckCapacity(1_001));
        }

        [TestMethod]
        public async Task Extract_StorageViolationStopsAndReapsTheWritingProcess()
        {
            using var folder = new TemporaryDirectory();
            var script = Path.Combine(folder.Path, "x");
            await File.WriteAllTextAsync(script, "import time\nwith open('payload', 'ab', buffering=0) as f:\n while True:\n  f.write(bytes(4096))\n  time.sleep(0.01)\n");
            var payload = Path.Combine(folder.Path, "payload");
            var tools = new ArchiveTools { SevenZipPath = Environment.GetEnvironmentVariable("JULARR_TEST_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3") };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<ServiceUnavailableException>(() => tools.ExtractAsync("unused", folder.Path, timeout.Token, () =>
            {
                if (File.Exists(payload) && new FileInfo(payload).Length >= 4_096) throw new ServiceUnavailableException("usenet_disk_budget");
            }));
            var stoppedLength = new FileInfo(payload).Length;
            await Task.Delay(100);
            Assert.AreEqual(stoppedLength, new FileInfo(payload).Length);
        }

        internal static async Task ConfigureAsync(Host host, string folder, DownloadStorageArea area, long? maxJobBytes = null)
        {
            Directory.CreateDirectory(Path.Combine(folder, "Incomplete"));
            Directory.CreateDirectory(Path.Combine(folder, "Complete"));
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("Downloads " + area, StorageProtocolType.Local, null, folder, null, null, null, false, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var mountId = (await created.Content.ReadFromJsonAsync<CreateStorageMountResultV1>())!.Id;
            using var configured = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/downloads/" + (int)area,
                new SaveDownloadStorageDataV1(mountId, "Incomplete", mountId, "Complete", 0, maxJobBytes, null, 2, 1, true));
            Assert.AreEqual(HttpStatusCode.OK, configured.StatusCode, await configured.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Storage_RequiresExplicitConfirmationAndPersistsSeparateAreas()
        {
            using var folder = new TemporaryDirectory();
            Directory.CreateDirectory(Path.Combine(folder.Path, "Incomplete"));
            Directory.CreateDirectory(Path.Combine(folder.Path, "Complete"));
            await using var host = await LibraryTests.CreateHostAsync(folder.Path);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("Downloads", StorageProtocolType.Local, null, folder.Path, null, null, null, false, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var mountId = (await created.Content.ReadFromJsonAsync<CreateStorageMountResultV1>())!.Id;
            // A native download folder can be created within a configured local mount from the picker.
            using (var createdFolder = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/download-directory",
                new CreateDownloadDirectoryDataV1("", "Picker created")))
            {
                Assert.AreEqual(HttpStatusCode.OK, createdFolder.StatusCode, await createdFolder.Content.ReadAsStringAsync());
                Assert.IsTrue(Directory.Exists(Path.Combine(folder.Path, "Picker created")));
            }
            using (var traversal = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/download-directory",
                new CreateDownloadDirectoryDataV1("", "../outside")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, traversal.StatusCode);
                Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, "..", "outside")));
            }
            const string route = "/api/v1/admin/storage/downloads/1";
            using var empty = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode, await empty.Content.ReadAsStringAsync());
            var initial = (await empty.Content.ReadFromJsonAsync<DownloadStorageSettingsV1>())!;
            Assert.IsNull(initial.IncompleteStorageMountId);
            Assert.IsNull(initial.FreeSpaceReserveBytes);
            Assert.IsNull(initial.MaxJobBytes);
            Assert.IsNull(initial.MaxWorkspaceBytes);

            using var unconfirmed = await host.SendAsync(HttpMethod.Post, route,
                new SaveDownloadStorageDataV1(mountId, "Incomplete", mountId, "Complete", 0, null, null, 2, initial.Version, false));
            Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            using var confirmed = await host.SendAsync(HttpMethod.Post, route,
                new SaveDownloadStorageDataV1(mountId, "Incomplete", mountId, "Complete", 0, null, null, 2, initial.Version, true));
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
            using var reread = await host.SendAsync(HttpMethod.Get, route);
            var saved = (await reread.Content.ReadFromJsonAsync<DownloadStorageSettingsV1>())!;
            Assert.AreEqual(mountId, saved.IncompleteStorageMountId);
            Assert.AreEqual("Complete", saved.CompletePath);
            Assert.AreEqual(0L, saved.FreeSpaceReserveBytes);
            Assert.IsFalse(string.IsNullOrEmpty(saved.IncompleteFilesystemIdentity));
            Assert.AreEqual(initial.Version + 1, saved.Version);
            Assert.AreEqual(0, Directory.EnumerateFiles(folder.Path, ".jularr-storage-probe-*", SearchOption.AllDirectories).Count());
            using var mount = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/storage/mounts/" + mountId);
            var connection = (await mount.Content.ReadFromJsonAsync<StorageMountDetailsV1>())!;
            using var removal = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/removal", new RemoveStorageMountDataV1(connection.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, removal.StatusCode, await removal.Content.ReadAsStringAsync());
            StringAssert.Contains(await removal.Content.ReadAsStringAsync(), "in_use");
            using var direct = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/storage/downloads/2");
            Assert.IsNull((await direct.Content.ReadFromJsonAsync<DownloadStorageSettingsV1>())!.IncompleteStorageMountId);

            using var stale = await host.SendAsync(HttpMethod.Post, route,
                new SaveDownloadStorageDataV1(mountId, "Incomplete", mountId, "Complete", 0, null, null, 2, initial.Version, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var overlap = await host.SendAsync(HttpMethod.Post, route,
                new SaveDownloadStorageDataV1(mountId, "", mountId, "Complete", 0, null, null, 2, saved.Version, true));
            Assert.AreNotEqual(HttpStatusCode.OK, overlap.StatusCode);
        }
    }
}
