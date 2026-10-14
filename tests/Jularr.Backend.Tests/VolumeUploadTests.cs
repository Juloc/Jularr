using System.Net;
using System.Net.Http.Headers;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class VolumeUploadTests
    {
        [TestMethod]
        public async Task Upload_MangaVolume_LandsInItsLibraryOnce()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            Directory.CreateDirectory(Path.Combine(storage.Path, mountId.ToString(), "Manga"));
            await LibraryTests.CreateRootAsync(host, mountId, "Manga", MediaType.Book, MediaSubtype.Manga);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES (70, 3, 2, 'Blue Sea');
                UPDATE "StorageMounts" SET "IsReadOnly" = FALSE;
                """);
            const string route = "/api/v1/admin/works/70/volumes";

            // Off by default.
            using var off = await host.SendAsync(HttpMethod.Post, route, Volume("v3.cbz", "3"));
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Uploads'");

            // A volume takes the import layout and belongs to its unit.
            using var uploaded = await host.SendAsync(HttpMethod.Post, route, Volume("v3.cbz", "3"));
            Assert.AreEqual(HttpStatusCode.OK, uploaded.StatusCode, await uploaded.Content.ReadAsStringAsync());
            Assert.AreEqual("pages", File.ReadAllText(Path.Combine(storage.Path, mountId.ToString(), "Manga", "Blue Sea", "Blue Sea v03.cbz")));
            Assert.AreEqual(3m, await host.ScalarAsync("""
                SELECT unit."Number" FROM "StoredFiles" AS file JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                WHERE file."RelativePath" = 'Blue Sea/Blue Sea v03.cbz'
                """));

            // The same volume again, or another kind of file, is refused.
            using var again = await host.SendAsync(HttpMethod.Post, route, Volume("again.cbz", "3"));
            StringAssert.Contains(await again.Content.ReadAsStringAsync(), "duplicate_value");
            using var wrong = await host.SendAsync(HttpMethod.Post, route, Volume("v4.exe", "4"));
            StringAssert.Contains(await wrong.Content.ReadAsStringAsync(), "invalid_file_type");
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(storage.Path, mountId.ToString(), "Manga", "Blue Sea"), "*.partial").Any());
        }

        private static MultipartFormDataContent Volume(string fileName, string volume)
        {
            var file = new ByteArrayContent("pages"u8.ToArray());
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return new MultipartFormDataContent { { file, "File", fileName }, { new StringContent(volume), "VolumeNumber" } };
        }
    }
}
