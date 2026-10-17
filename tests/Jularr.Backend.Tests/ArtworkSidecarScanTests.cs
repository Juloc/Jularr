using System.Net;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArtworkSidecarScanTests
    {
        [TestMethod]
        public async Task MovieSidecars_AreLinkedOnlyAfterIdentityAndAreReconciledOnCompleteScan()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var movieFolder = Path.Combine(storage.Path, mountId.ToString(), "Movies", "Film (2001)");
            Directory.CreateDirectory(movieFolder);
            File.WriteAllText(Path.Combine(movieFolder, "Film.2001.mkv"), "video");
            File.WriteAllText(Path.Combine(movieFolder, "poster.jpg"), "sidecar");
            File.WriteAllText(Path.Combine(movieFolder, "fanart.jxl"), "sidecar");
            File.WriteAllText(Path.Combine(movieFolder, "cover-story.jpg"), "not a cover");
            File.WriteAllText(Path.Combine(movieFolder, "season01-poster.jpg"), "no season WorkUnit");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);

            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\""));

            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Film", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" WHERE \"SourceKindId\" = 2"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(DISTINCT \"WorkId\") FROM \"WorkArtworks\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" WHERE \"SourceKey\" LIKE '%poster.jpg' AND \"SlotId\" = 1"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" WHERE \"SourceKey\" LIKE '%fanart.jxl' AND \"SlotId\" = 2"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\""));

            File.Delete(Path.Combine(movieFolder, "poster.jpg"));
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" WHERE \"SlotId\" = 2"));

            // A scan of an unavailable root never treats its artwork as deleted.
            var libraryPath = Path.GetDirectoryName(movieFolder)!;
            Directory.Move(libraryPath, libraryPath + ".offline");
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\""));
        }

        [TestMethod]
        public async Task SeriesSeasonArtwork_UsesCanonicalSeasonUnit()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var series = Path.Combine(storage.Path, mountId.ToString(), "Series", "Show", "Season 1");
            Directory.CreateDirectory(series);
            File.WriteAllText(Path.Combine(series, "Show - S01E01.mkv"), "video");
            var coverFolder = Directory.GetParent(series)!.FullName;
            File.WriteAllText(Path.Combine(coverFolder, "poster.jpg"), "series artwork");
            File.WriteAllText(Path.Combine(coverFolder, "season01-poster.jpg"), "season artwork");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Series", MediaType.Series);
            await LibraryTests.ScanAsync(host, rootId);

            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                new AssignLibraryFileDataV1(null, "Show", WorkUnitType.Episode, 1m, 1m));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());

            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" WHERE \"WorkUnitId\" IS NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\" AS artwork JOIN \"WorkUnits\" AS unit ON unit.\"Id\" = artwork.\"WorkUnitId\" WHERE unit.\"WorkUnitTypeId\" = 1 AND unit.\"Number\" = 1"));
        }

        [TestMethod]
        public async Task RootLevelArtwork_WithSeveralWorksIsNotAssignedArbitrarily()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootFolder = Path.Combine(storage.Path, mountId.ToString(), "Movies");
            Directory.CreateDirectory(rootFolder);
            File.WriteAllText(Path.Combine(rootFolder, "One.mkv"), "video");
            File.WriteAllText(Path.Combine(rootFolder, "Two.mkv"), "video");
            File.WriteAllText(Path.Combine(rootFolder, "poster.jpg"), "ambiguous");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);

            foreach (var title in new[] { "One", "Two" })
            {
                var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" = '" + title + ".mkv'"))!;
                using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment",
                    new AssignLibraryFileDataV1(null, title, null, null, null));
                Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            }

            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\""));
        }
    }
}
