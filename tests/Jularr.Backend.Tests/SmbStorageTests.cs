using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SmbStorageTests
    {
        private static readonly byte[] Film = [.. Enumerable.Range(0, 5000).Select(index => (byte)(index % 251))];

        [TestMethod]
        public async Task Files_DirectShare_ReadWriteMoveListAndProbeLikeALocalFolder()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            Directory.CreateDirectory(Path.Combine(folder.Path, "media"));
            var store = new FakeSmbStore(folder.Path);
            using var share = new SmbShare(() => new SmbSession(store, () => { }), TimeProvider.System);
            var files = new SmbStorageFiles(share, "media");
            var ct = CancellationToken.None;

            // Written in chunks the server accepts, read back from any position.
            await using (var created = await files.CreateAsync("Film (2020)/Film.mkv", ct))
            {
                await created!.WriteAsync(Film, ct);
            }

            Assert.IsNull(await files.CreateAsync("Film (2020)/Film.mkv", ct));
            await using (var read = await files.OpenReadAsync("Film (2020)/Film.mkv", ct))
            {
                Assert.AreEqual(5000L, read.Length);
                read.Seek(3900, SeekOrigin.Begin);
                var tail = new byte[1100];
                await read.ReadExactlyAsync(tail, ct);
                CollectionAssert.AreEqual(Film[3900..], tail);
            }

            // Hidden names stay out of listings and walks.
            File.WriteAllText(Path.Combine(folder.Path, "media", ".hidden"), "x");
            File.WriteAllText(Path.Combine(folder.Path, "media", "other.mkv"), "other");
            CollectionAssert.AreEqual(new[] { "Film (2020)", "other.mkv" }, (await files.ListAsync("", ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray());
            var walked = new List<string>();
            await foreach (var file in files.EnumerateFilesAsync("", null, new StorageScanState(), ct))
            {
                walked.Add(file.RelativePath + ":" + file.SizeBytes);
            }

            CollectionAssert.AreEquivalent(new[] { "Film (2020)/Film.mkv:5000", "other.mkv:5" }, walked);

            // A move never replaces and creates the folders it needs; only empty folders go.
            Assert.IsFalse(await files.MoveAsync("other.mkv", "Film (2020)/Film.mkv", ct));
            Assert.IsTrue(await files.MoveAsync("other.mkv", "Other (2021)/Other.mkv", ct));
            Assert.IsFalse(await files.DeleteDirectoryAsync("Other (2021)", ct));
            await files.DeleteFileAsync("Other (2021)/Other.mkv", ct);
            await files.DeleteEmptyFoldersAsync("Other (2021)", "", ct);
            Assert.IsNull(await files.GetAsync("Other (2021)", ct));
            Assert.IsNull(await files.GetAsync("missing.mkv", ct));
            await Assert.ThrowsExactlyAsync<NasMountException>(() => files.OpenReadAsync("../secret", ct));
            await Assert.ThrowsExactlyAsync<NasMountException>(() => files.OpenReadAsync("missing.mkv", ct));

            var probe = await files.ProbeAsync(ct);
            Assert.IsTrue(probe.IsReadable);
            Assert.AreEqual(250L * 4096, probe.FreeBytes);
            Assert.AreEqual(1000L * 4096, probe.TotalBytes);
            Assert.AreEqual(0, store.OpenHandles);
        }

        [TestMethod]
        public async Task Share_LostSessionAndIdleTime_OpenAgainOnlyWhenNeeded()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            File.WriteAllBytes(Path.Combine(folder.Path, "a.mkv"), Film);
            var store = new FakeSmbStore(folder.Path);
            var clock = new Clock();
            var (connects, closes) = (0, 0);
            using var share = new SmbShare(() =>
            {
                connects++;
                return new SmbSession(store, () => closes++);
            }, clock);
            var files = new SmbStorageFiles(share, "");
            var ct = CancellationToken.None;

            Assert.IsNotNull(await files.GetAsync("a.mkv", ct));
            Assert.AreEqual(1, connects);

            // A dropped session fails the request and its open streams; the next request opens a new one.
            var stream = await files.OpenReadAsync("a.mkv", ct);
            store.IsLost = true;
            await Assert.ThrowsExactlyAsync<NasMountException>(() => files.ListAsync("", ct));
            store.IsLost = false;
            Assert.ThrowsExactly<IOException>(() => stream.ReadByte());
            await stream.DisposeAsync();
            Assert.AreEqual(1, (await files.ListAsync("", ct)).Count);
            Assert.AreEqual((2, 1), (connects, closes));

            // An open stream keeps the session; after 15 idle minutes it closes and the NAS may sleep.
            stream = await files.OpenReadAsync("a.mkv", ct);
            clock.Advance(TimeSpan.FromMinutes(20));
            share.CloseIfIdle(SmbShares.IdleTime);
            Assert.AreEqual(1, closes);
            await stream.DisposeAsync();
            clock.Advance(TimeSpan.FromMinutes(14));
            share.CloseIfIdle(SmbShares.IdleTime);
            Assert.AreEqual(1, closes);
            clock.Advance(TimeSpan.FromMinutes(2));
            share.CloseIfIdle(SmbShares.IdleTime);
            Assert.AreEqual(2, closes);
            Assert.IsNotNull(await files.GetAsync("a.mkv", ct));
            Assert.AreEqual(3, connects);
        }

        [TestMethod]
        public async Task Inputs_DirectShareFile_AreSeekableLoopbackAddressesWhileTheToolRuns()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            File.WriteAllBytes(Path.Combine(folder.Path, "Film.mkv"), Film);
            using var share = new SmbShare(() => new SmbSession(new FakeSmbStore(folder.Path), () => { }), TimeProvider.System);
            using var server = new StorageStreamServer();
            using var http = new HttpClient();

            var input = server.Open(new SmbStorageFiles(share, ""), "Film.mkv");
            StringAssert.StartsWith(input.Location, "http://127.0.0.1:");
            StringAssert.EndsWith(input.Location, "/Film.mkv");
            using var whole = await http.GetAsync(input.Location);
            Assert.AreEqual(HttpStatusCode.OK, whole.StatusCode);
            CollectionAssert.AreEqual(Film, await whole.Content.ReadAsByteArrayAsync());

            using var request = new HttpRequestMessage(HttpMethod.Get, input.Location);
            request.Headers.Range = new RangeHeaderValue(1000, 1999);
            using var part = await http.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.PartialContent, part.StatusCode);
            Assert.AreEqual("bytes 1000-1999/5000", part.Content.Headers.ContentRange?.ToString());
            CollectionAssert.AreEqual(Film[1000..2000], await part.Content.ReadAsByteArrayAsync());

            // Done: the address answers no more.
            var location = input.Location;
            input.Dispose();
            using var gone = await http.GetAsync(location);
            Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);

            // A folder on this host is read where it is.
            using var local = server.Open(new LocalStorageFiles(folder.Path), "Film.mkv");
            Assert.AreEqual(Path.Combine(folder.Path, "Film.mkv"), local.Location);
        }

        [TestMethod]
        public async Task Connections_DirectSmb_CheckBrowseAndScanThroughJularrsOwnSession()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("NAS", StorageProtocolType.Smb, "nas.test", "/media/library", "3.1.1", "jularr", null, true, "secret"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var mountId = (await created.Content.ReadFromJsonAsync<CreateStorageMountResultV1>())!.Id;
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsVolume\" FROM \"StorageMounts\""));

            // The share "media" with the folder "library"; the session is the test's own store instead of a server.
            var share = Path.Combine(storage.Path, "share");
            Directory.CreateDirectory(Path.Combine(share, "library", "Movies", "Film (2020)"));
            File.WriteAllBytes(Path.Combine(share, "library", "Movies", "Film (2020)", "Film (2020).mkv"), Film);
            host.Services.GetRequiredService<SmbShares>().Get(mountId, 1, () => new SmbSession(new FakeSmbStore(share), () => { }));

            using var check = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/check");
            Assert.AreEqual(HttpStatusCode.OK, check.StatusCode, await check.Content.ReadAsStringAsync());
            Assert.AreEqual(StorageAvailabilityType.Available, (await check.Content.ReadFromJsonAsync<StorageMountStatusV1>())!.StorageAvailabilityTypeId);
            using var browse = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/directories", new ReadStorageDirectoriesDataV1(""));
            StringAssert.Contains(await browse.Content.ReadAsStringAsync(), "Movies");

            // No compose lines: nothing is mounted by Docker.
            using var volume = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/storage/mounts/" + mountId + "/volume");
            Assert.AreEqual(HttpStatusCode.BadRequest, volume.StatusCode);

            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual("Film (2020)/Film (2020).mkv", await host.ScalarAsync("SELECT \"RelativePath\" FROM \"StoredFiles\""));
            Assert.AreEqual(5000L, await host.ScalarAsync("SELECT \"SizeBytes\" FROM \"StoredFiles\""));
        }

        private sealed class Clock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
