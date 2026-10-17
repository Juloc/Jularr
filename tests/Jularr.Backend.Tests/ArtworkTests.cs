using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArtworkTests
    {
        [TestMethod]
        public void FileNames_RecognizeLocalArtworkWithoutAcceptingUnknownOrUnsafePaths()
        {
            Assert.AreEqual(new ArtworkSidecar(ArtworkSlot.Poster), ArtworkFileNames.Recognize("Movie (2014)/poster.JPG"));
            Assert.AreEqual(new ArtworkSidecar(ArtworkSlot.Poster), ArtworkFileNames.Recognize("books/book/folder.png"));
            Assert.AreEqual(new ArtworkSidecar(ArtworkSlot.Backdrop), ArtworkFileNames.Recognize("Movies/Film/fanart.jxl"));
            Assert.AreEqual(new ArtworkSidecar(ArtworkSlot.Logo), ArtworkFileNames.Recognize("Movies/Film/clearlogo.png"));
            Assert.AreEqual(new ArtworkSidecar(ArtworkSlot.Poster, 1), ArtworkFileNames.Recognize("Shows/Series/season01-poster.jpg"));
            Assert.AreEqual(new ArtworkSidecar(ArtworkSlot.Poster, 0), ArtworkFileNames.Recognize("Shows/Series/season00-poster.webp"));

            foreach (var unsafePath in new[] { "../poster.jpg", "/absolute/poster.jpg", "Series//poster.jpg", "Series/.hidden/poster.jpg",
                "Series\\poster.jpg", "poster.jpg.exe", "not-a-poster.jpg", "seasonab-poster.jpg", "season1000-poster.jpg" })
            {
                Assert.IsNull(ArtworkFileNames.Recognize(unsafePath), unsafePath);
            }
        }

        [TestMethod]
        public void ExportNames_AreCompatibleByDefaultAndJxlWhenRequested()
        {
            Assert.AreEqual("poster.jpg", ArtworkFileNames.ExportName(ArtworkSlot.Poster, false));
            Assert.AreEqual("fanart.jpg", ArtworkFileNames.ExportName(ArtworkSlot.Backdrop, false));
            Assert.AreEqual("clearlogo.png", ArtworkFileNames.ExportName(ArtworkSlot.Logo, false));
            Assert.AreEqual("poster.jxl", ArtworkFileNames.ExportName(ArtworkSlot.Poster, true));
            Assert.IsTrue(ArtworkFileNames.SourceRank(ArtworkSourceKind.Sidecar, false) <
                          ArtworkFileNames.SourceRank(ArtworkSourceKind.Provider, false));
            Assert.AreEqual(0, ArtworkFileNames.SourceRank(ArtworkSourceKind.Provider, true));
        }

        [TestMethod]
        public async Task Baseline_StoresArtworkSettingsAndCandidatesWithoutDuplicateIdentities()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(folder.Path);
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"NasModeId\" FROM \"ArtworkSettings\""));
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"CacheFormatId\" FROM \"ArtworkSettings\""));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ProgressiveJxl\" FROM \"ArtworkSettings\""));

            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Artwork Film');
                INSERT INTO "WorkArtworks" ("WorkId", "SlotId", "SourceKindId", "SourceKey")
                    SELECT "Id", 1, 3, 'tmdb:poster:001' FROM "Works" WHERE "CanonicalTitle" = 'Artwork Film';
                INSERT INTO "WorkArtworks" ("WorkId", "SlotId", "SourceKindId", "SourceKey", "IsPinned")
                    SELECT "Id", 1, 1, 'manual:cover:001', TRUE FROM "Works" WHERE "CanonicalTitle" = 'Artwork Film';
                INSERT INTO "WorkArtworks" ("WorkId", "SlotId", "SourceKindId", "SourceKey")
                    SELECT "Id", 1, 3, 'tmdb:poster:001' FROM "Works" WHERE "CanonicalTitle" = 'Artwork Film'
                    ON CONFLICT DO NOTHING;
                """);

            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"WorkArtworks\""));
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ArtworkFallbacks" ("WorkId", "SlotId", "WebpBytes", "ContentHash")
                    SELECT "Id", 1, decode('52494646', 'hex'), repeat('a', 64) FROM "Works" WHERE "CanonicalTitle" = 'Artwork Film';
                """);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ArtworkFallbacks\""));
        }
    }
}
