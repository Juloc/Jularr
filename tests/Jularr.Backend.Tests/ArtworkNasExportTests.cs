using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Logic.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Jularr.Workers;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArtworkNasExportTests
    {
        private const string Route = "/api/v1/admin/works/";
        private const string PosterUrl = "https://image.tmdb.org/t/p/w500/test-poster.png";

        [TestMethod]
        public async Task ProviderPoster_ExportsJpegBesideTheMediaOnceWithoutOverwritingIt()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
                services.AddHttpClient<ArtworkImageDownloader>().ConfigurePrimaryHttpMessageHandler(() => new ImageHandler()));
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE WHERE \"Id\" = " + mountId);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Film (2001)");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Film.mkv"), "video");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);

            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Film", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'Film'"))!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual")
                VALUES ({workId}, 'Poster', '{PosterUrl}', TRUE);
                """);

            using var exported = await host.SendAsync(HttpMethod.Post, Route + workId + "/artwork/export");
            Assert.AreEqual(HttpStatusCode.OK, exported.StatusCode, await exported.Content.ReadAsStringAsync());
            var result = (await exported.Content.ReadFromJsonAsync<ArtworkExportResultV1>())!;
            Assert.AreEqual(1, result.SavedCount);
            Assert.AreEqual(0, result.SkippedCount);
            var poster = Path.Combine(folder, "poster.jpg");
            Assert.IsTrue(File.Exists(poster));
            var bytes = await File.ReadAllBytesAsync(poster);
            Assert.IsTrue(bytes.Length > 4 && bytes[0] == 255 && bytes[1] == 216, "NAS artwork must be actual JPEG bytes.");
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" WHERE \"SourceKindId\" = 2 AND \"SourceKey\" = 'Film (2001)/poster.jpg'"));

            using var repeated = await host.SendAsync(HttpMethod.Post, Route + workId + "/artwork/export");
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
            Assert.AreEqual(0, (await repeated.Content.ReadFromJsonAsync<ArtworkExportResultV1>())!.SavedCount);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(poster));
        }

        [TestMethod]
        public async Task ReadOnlyNasAndCustomArtwork_AreNeverModified()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
                services.AddHttpClient<ArtworkImageDownloader>().ConfigurePrimaryHttpMessageHandler(() => new ImageHandler()));
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Protected");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Protected.mkv"), "video");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Protected", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode);
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'Protected'"))!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual")
                VALUES ({workId}, 'Poster', '{PosterUrl}', TRUE);
                """);

            using (var deniedWrite = await host.SendAsync(HttpMethod.Post, Route + workId + "/artwork/export"))
            {
                Assert.AreEqual(HttpStatusCode.OK, deniedWrite.StatusCode);
                Assert.AreEqual(0, (await deniedWrite.Content.ReadFromJsonAsync<ArtworkExportResultV1>())!.SavedCount);
                Assert.IsFalse(File.Exists(Path.Combine(folder, "poster.jpg")));
            }

            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE WHERE \"Id\" = " + mountId);
            var original = new byte[] { 3, 4, 5, 6 };
            await File.WriteAllBytesAsync(Path.Combine(folder, "cover.png"), original);
            using var protectedArt = await host.SendAsync(HttpMethod.Post, Route + workId + "/artwork/export");
            Assert.AreEqual(HttpStatusCode.OK, protectedArt.StatusCode);
            Assert.AreEqual(0, (await protectedArt.Content.ReadFromJsonAsync<ArtworkExportResultV1>())!.SavedCount);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(folder, "cover.png")));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "poster.jpg")));

            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var unauthorized = await host.SendAsync(HttpMethod.Post, Route + workId + "/artwork/export");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        [TestMethod]
        public void ExportFolder_RejectsSharedRootsAndUnderstandsSeasonFolders()
        {
            Assert.IsNull(ArtworkExportDirectory.For(MediaType.Movie, "film.mkv"));
            Assert.AreEqual("Show", ArtworkExportDirectory.For(MediaType.Series, "Show/Season 01/Episode.mkv"));
            Assert.AreEqual("Show", ArtworkExportDirectory.For(MediaType.Series, "Show/S01/Episode.mkv"));
            Assert.AreEqual("Show", ArtworkExportDirectory.For(MediaType.Series, "Show/Specials/Episode.mkv"));
            Assert.AreEqual("Book", ArtworkExportDirectory.For(MediaType.Book, "Book/chapter.epub"));
            Assert.IsNull(ArtworkExportDirectory.For(MediaType.Series, "S01/Episode.mkv"));
        }


        [TestMethod]
        public async Task AssignmentWithMetadata_QueuesOneBackgroundExportAndPublishesSidecar()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
                services.AddHttpClient<ArtworkImageDownloader>().ConfigurePrimaryHttpMessageHandler(() => new ImageHandler()));
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE WHERE \"Id\" = " + mountId);
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Queued (2001)");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Queued.mkv"), "video");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);

            // Metadata is already known before the media file is assigned.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Queued');
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual")
                    SELECT "Id", 'Poster', '{PosterUrl}', TRUE FROM "Works" WHERE "CanonicalTitle" = 'Queued';
                """);
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'Queued'"))!;
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(workId, null, null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 4 AND \"OperationStatusTypeId\" = 1"));

            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await OperationWorker.RunNextAsync(scope.ServiceProvider, NullLogger.Instance, CancellationToken.None));
            }

            Assert.IsTrue(File.Exists(Path.Combine(folder, "poster.jpg")));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 4"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" WHERE \"SourceKindId\" = 2"));
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 4"));
        }

        [TestMethod]
        public async Task JxlOnlyMode_DoesNotQueueUnsupportedJpegExport()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE WHERE \"Id\" = " + mountId);
            await host.Fixture.RunSqlAsync("UPDATE \"ArtworkSettings\" SET \"NasModeId\" = 3");
            var folder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Jxl");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Jxl.mkv"), "video");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Jxl", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode);
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\" WHERE \"CanonicalTitle\" = 'Jxl'"))!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual")
                VALUES ({workId}, 'Poster', '{PosterUrl}', TRUE);
                """);
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 4"));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "poster.jpg")));
        }

        private sealed class ImageHandler : HttpMessageHandler
        {
            // Small opaque PNG; the production image converter must return actual JPEG bytes.
            private static readonly byte[] Png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPUqNjCwMDAxMDAwMDAAAAPjgFYnAaHkgAAAABJRU5ErkJggg==");

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Assert.AreEqual(PosterUrl, request.RequestUri!.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
            }
        }
    }
}
