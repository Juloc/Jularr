using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        public async Task Share_OpenFilesOnOwnSessions_LostSessionsReopenAndIdleOnesClose()
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

            // Short requests share one session; every open file reads on a session of its own, so a playback never waits behind a scan.
            Assert.IsNotNull(await files.GetAsync("a.mkv", ct));
            var first = await files.OpenReadAsync("a.mkv", ct);
            var second = await files.OpenReadAsync("a.mkv", ct);
            Assert.AreEqual(0, first.ReadByte());
            Assert.AreEqual(0, second.ReadByte());
            Assert.AreEqual((3, 3), (connects, share.OpenSessions));

            // At most eight sessions; further files share the least busy one.
            var more = new List<Stream>();
            for (var index = 0; index < 8; index++)
            {
                var stream = await files.OpenReadAsync("a.mkv", ct);
                stream.ReadByte();
                more.Add(stream);
            }

            Assert.AreEqual(SmbShare.MaximumSessions, share.OpenSessions);
            foreach (var stream in more) await stream.DisposeAsync();

            // A dropped session fails the request and the files on it; the next request opens a new one.
            store.IsLost = true;
            await Assert.ThrowsExactlyAsync<NasMountException>(() => files.ListAsync("", ct));
            first.Position = 4000;
            Assert.ThrowsExactly<NasMountException>(() => first.Read(new byte[2000]));
            store.IsLost = false;
            Assert.ThrowsExactly<IOException>(() => first.Read(new byte[2000]));
            await first.DisposeAsync();
            Assert.AreEqual(1, (await files.ListAsync("", ct)).Count);

            // An open file keeps its session; after 15 idle minutes the others close and the NAS may sleep.
            clock.Advance(TimeSpan.FromMinutes(16));
            share.CloseIfIdle(SmbShares.IdleTime);
            Assert.AreEqual(1, share.OpenSessions);
            await second.DisposeAsync();
            clock.Advance(TimeSpan.FromMinutes(16));
            share.CloseIfIdle(SmbShares.IdleTime);
            Assert.AreEqual(0, share.OpenSessions);
            Assert.AreEqual(connects, closes);
            Assert.IsNotNull(await files.GetAsync("a.mkv", ct));
        }

        [TestMethod]
        public async Task Stream_ReadsAhead_SmallReadsAndSeeksReturnTheFileExactly()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            File.WriteAllBytes(Path.Combine(folder.Path, "a.mkv"), Film);
            using var share = new SmbShare(() => new SmbSession(new FakeSmbStore(folder.Path), () => { }), TimeProvider.System);
            await using var stream = await new SmbStorageFiles(share, "").OpenReadAsync("a.mkv", CancellationToken.None);

            // Reads smaller than a piece and across piece borders, as a player's copy loop does.
            using var copy = new MemoryStream();
            var buffer = new byte[7];
            for (int read; (read = stream.Read(buffer)) > 0;) copy.Write(buffer, 0, read);
            CollectionAssert.AreEqual(Film, copy.ToArray());

            // Back and forth; a piece fetched ahead and passed over does no harm.
            foreach (var position in new long[] { 4321, 10, 2999, 4999, 0 })
            {
                stream.Position = position;
                var part = new byte[Math.Min(1500, Film.Length - (int)position)];
                stream.ReadExactly(part);
                CollectionAssert.AreEqual(Film[(int)position..((int)position + part.Length)], part, "at " + position);
            }
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
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
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

            Directory.CreateDirectory(Path.Combine(share, "library", "Books & more"));
            using var nextFolders = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts/" + mountId + "/directories", new ReadStorageDirectoriesDataV1("", "Books & more"));
            CollectionAssert.AreEqual(new[] { "Movies" }, (await nextFolders.Content.ReadFromJsonAsync<StorageDirectoryV1[]>())!.Select(directory => directory.Name).ToArray());
            using var fragment = await host.SendAsync(HttpMethod.Get, "/Setup/Storage?handler=Folders&storageMountId=" + mountId);
            var html = await fragment.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, fragment.StatusCode, html);
            StringAssert.Contains(html, "data-folder-fragment");
            StringAssert.Contains(html, "Books &amp; more");
            Assert.IsFalse(html.Contains("<html", StringComparison.OrdinalIgnoreCase));
            using var traversal = await host.SendAsync(HttpMethod.Get, "/Setup/Storage?handler=Folders&storageMountId=" + mountId + "&path=..%2F..%2Foutside");
            Assert.AreEqual(HttpStatusCode.BadRequest, traversal.StatusCode);

            // No compose lines: nothing is mounted by Docker.
            using var volume = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/storage/mounts/" + mountId + "/volume");
            Assert.AreEqual(HttpStatusCode.BadRequest, volume.StatusCode);

            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual("Film (2020)/Film (2020).mkv", await host.ScalarAsync("SELECT \"RelativePath\" FROM \"StoredFiles\""));
            Assert.AreEqual(5000L, await host.ScalarAsync("SELECT \"SizeBytes\" FROM \"StoredFiles\""));
        }

        [TestMethod]
        public async Task Playback_DirectSmbShare_StreamsRangesAndPreservesResume()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/mounts",
                new SaveStorageMountDataV1("NAS", StorageProtocolType.Smb, "nas.test", "/media/library", "3.1.1", "jularr", null, true, "secret"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var mountId = (await created.Content.ReadFromJsonAsync<CreateStorageMountResultV1>())!.Id;

            // The same direct SMB path is used for scanning, planning and browser range requests.
            var share = Path.Combine(storage.Path, "share");
            var movie = Path.Combine(share, "library", "Movies", "Film (2020)");
            Directory.CreateDirectory(movie);
            File.WriteAllBytes(Path.Combine(movie, "Film.mp4"), Film);
            host.Services.GetRequiredService<SmbShares>().Get(mountId, 1, () => new SmbSession(new FakeSmbStore(share), () => { }));
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;

            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Film", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            var assetId = (await assigned.Content.ReadFromJsonAsync<AssignLibraryFileResultV1>())!.MediaAssetId;
            using var player = await host.SendAsync(HttpMethod.Get, "/Player/" + assetId);
            Assert.AreEqual(HttpStatusCode.OK, player.StatusCode, await player.Content.ReadAsStringAsync());

            var capabilities = new StartPlaybackDataV1(["mp4"], ["h264"], ["aac"]);
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", capabilities);
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            var plan = (await started.Content.ReadFromJsonAsync<PlaybackPlanV1>())!;
            Assert.AreEqual(PlaybackModeType.DirectPlay, plan.PlaybackModeTypeId);

            using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl);
            request.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            request.Headers.Range = new RangeHeaderValue(1000, 1999);
            using var partial = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode, await partial.Content.ReadAsStringAsync());
            Assert.AreEqual("bytes 1000-1999/5000", partial.Content.Headers.ContentRange?.ToString());
            Assert.AreEqual("video/mp4", partial.Content.Headers.ContentType?.MediaType);
            CollectionAssert.AreEqual(Film[1000..2000], await partial.Content.ReadAsByteArrayAsync());

            using var progress = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + plan.PlaybackSessionId,
                new UpdatePlaybackDataV1(2_500, 13_500, false, false));
            Assert.AreEqual(HttpStatusCode.OK, progress.StatusCode, await progress.Content.ReadAsStringAsync());
            using var resumed = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", capabilities);
            Assert.AreEqual(HttpStatusCode.OK, resumed.StatusCode, await resumed.Content.ReadAsStringAsync());
            Assert.AreEqual(2_500L, (await resumed.Content.ReadFromJsonAsync<PlaybackPlanV1>())!.ResumePositionMs);
        }

        [TestMethod]
        public async Task Inputs_DirectShare_RealFfmpegTranscodesAndSeeksHls()
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG") is not { Length: > 0 } ffmpeg)
            {
                Assert.Inconclusive("Set JULARR_TEST_FFMPEG to run real SMB-to-FFmpeg conversion.");
                return;
            }

            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            var source = Path.Combine(storage.Path, "Film.mkv");
            using (var generate = System.Diagnostics.Process.Start(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=12", "-f", "lavfi", "-i", "sine=frequency=440",
                "-t", "14", "-c:v", "mpeg4", "-c:a", "ac3", "-shortest", source]))
            {
                Assert.IsNotNull(generate);
                await generate.WaitForExitAsync();
                Assert.AreEqual(0, generate.ExitCode);
            }

            using var share = new SmbShare(() => new SmbSession(new FakeSmbStore(storage.Path), () => { }), TimeProvider.System);
            using var stream = new StorageStreamServer();
            using var transcodes = new PlaybackTranscodes(new MediaTranscoder { ExecutablePath = ffmpeg },
                Options.Create(new MediaOptions { TranscodeCachePath = cache.Path, MaxTranscodes = 1 }));
            var session = Guid.NewGuid();
            transcodes.Register(new PlaybackTranscodeSource(session, 7, stream.Open(new SmbStorageFiles(share, ""), "Film.mkv"),
                0, 1, false, false, 14_000));

            // FFmpeg reads via Jularr's loopback Range server backed by a real SMB file stream.
            // The later segment restarts input seeking rather than merely replaying the first bytes.
            var first = await transcodes.GetSegmentAsync(session, 7, 0, CancellationToken.None);
            var sought = await transcodes.GetSegmentAsync(session, 7, 1, CancellationToken.None);
            Assert.IsTrue(new FileInfo(first).Length > 188);
            Assert.IsTrue(new FileInfo(sought).Length > 188);
            Assert.AreEqual((byte)0x47, File.ReadAllBytes(sought)[0]);
            transcodes.Stop(session);
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(first)));
        }

        private sealed class Clock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
