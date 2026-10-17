using System.Net;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Media;
using SkiaSharp;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArtworkCacheTests
    {
        [TestMethod]
        public async Task NasPoster_IsCachedLocallyAndServedWithoutReopeningStorage()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var film = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Film");
            Directory.CreateDirectory(film);
            File.WriteAllText(Path.Combine(film, "Film.mkv"), "video");
            var imagePath = Path.Combine(film, "poster.png");
            await File.WriteAllBytesAsync(imagePath, ImageBytes(SKColors.CornflowerBlue));

            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Film", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            await LibraryTests.ScanAsync(host, rootId);

            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'Film'"))!;
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkRenditions\" WHERE \"FormatId\" = 3"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkFallbacks\""));
            Assert.AreEqual("image/webp", (await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/artwork/1")).Content.Headers.ContentType?.MediaType);

            using var first = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/artwork/1");
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            var bytes = await first.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(bytes.Length > 20 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F');

            var rootFolder = Path.GetDirectoryName(film)!;
            Directory.Move(rootFolder, rootFolder + ".offline");
            using var offline = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/artwork/1");
            Assert.AreEqual(HttpStatusCode.OK, offline.StatusCode, await offline.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(bytes, await offline.Content.ReadAsByteArrayAsync());
        }

        [TestMethod]
        public async Task ArtworkModification_InvalidatesRenditionAndLeavesOneLogicalVariant()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Film");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Film.mkv"), "video");
            var imagePath = Path.Combine(folder, "poster.png");
            await File.WriteAllBytesAsync(imagePath, ImageBytes(SKColors.Red));
            var root = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, root);
            var storedFileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + storedFileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Film", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode);
            await LibraryTests.ScanAsync(host, root);
            var keyBefore = (string)(await host.ScalarAsync("SELECT \"CacheKey\" FROM \"ArtworkRenditions\""))!;

            await File.WriteAllBytesAsync(imagePath, ImageBytes(SKColors.Green));
            File.SetLastWriteTimeUtc(imagePath, DateTime.UtcNow.AddSeconds(2));
            await LibraryTests.ScanAsync(host, root);
            var keyAfter = (string)(await host.ScalarAsync("SELECT \"CacheKey\" FROM \"ArtworkRenditions\""))!;
            Assert.AreNotEqual(keyBefore, keyAfter);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkRenditions\""));
        }

        [TestMethod]
        public async Task CacheBudgetDisabled_RetiresRenditionsButPreservesDatabasePreview()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Preview");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Preview.mkv"), "video");
            await File.WriteAllBytesAsync(Path.Combine(folder, "poster.png"), ImageBytes(SKColors.CadetBlue));
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);

            await LibraryTests.ScanAsync(host, rootId);
            var storedFileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + storedFileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Preview", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode);
            await LibraryTests.ScanAsync(host, rootId);

            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'Preview'"))!;
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkRenditions\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkFallbacks\""));

            await host.Fixture.RunSqlAsync("UPDATE \"ArtworkSettings\" SET \"CacheLimitBytes\" = 0");
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkRenditions\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkFallbacks\""));

            // Never advertise removed variants; the database-sized WebP still works offline.
            Directory.Move(Path.GetDirectoryName(folder)!, Path.GetDirectoryName(folder)! + ".offline");
            using var offline = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/artwork/1");
            Assert.AreEqual(HttpStatusCode.OK, offline.StatusCode, await offline.Content.ReadAsStringAsync());
            Assert.AreEqual("image/webp", offline.Content.Headers.ContentType?.MediaType);
            Assert.IsTrue((await offline.Content.ReadAsByteArrayAsync()).Length > 16);
        }

        private static byte[] ImageBytes(SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(160, 240));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
