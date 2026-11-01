using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Media.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DetailsPageTests
    {
        [TestMethod]
        public async Task Work_ReadsRatingProgressEditionsAndUnitFacts_AndTheTabsShowThem()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "OriginalLanguage") VALUES (10, 3, 'The Book', 2020, 'en');
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES (10, 'Poster', 'https://example.test/poster.webp', TRUE);
                UPDATE "Works" SET "OriginalTitle" = 'Das Buch', "Rating" = 8.2, "Votes" = 50, "CatalogNamespace" = 'openlibrary' WHERE "Id" = 10;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (10, 'openlibrary', 'OL1W');
                INSERT INTO "WorkEditions" ("Id", "WorkId", "Name", "LanguageCode", "EditionFormatId", "Publisher") VALUES (1, 10, 'English EPUB', 'en', 2, 'Press'), (2, 10, 'Deutsch EPUB', 'de', 2, NULL);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (1, 10, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number", "Title", "ImageUrl") VALUES (101, 10, 3, 1, 'First', 'https://example.test/v1.jpg'), (102, 10, 3, 2, 'Second', NULL);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId") VALUES (1, 10, 1, 101, 3);
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId") VALUES ({rootId}, 'The Book/1.epub', 1, now(), now(), 1);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "Locator", "Progression")
                SELECT (SELECT "ActiveProfileId" FROM "AccountSessions" WHERE "ActiveProfileId" IS NOT NULL ORDER BY "CreatedAt" DESC LIMIT 1), 10, 101, 2, 'p10', 0.5;
                """);

            // The Work carries what its hero shows: the catalog's rating with its source, the card's availability and progress.
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10");
            var work = (await read.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            Assert.AreEqual(8.2m, work.Rating);
            Assert.AreEqual("openlibrary", work.RatingSource);
            Assert.AreEqual("Das Buch", work.OriginalTitle);
            Assert.AreEqual(ProgressStateType.InProgress, work.ProgressStateId);
            Assert.IsTrue(work.AvailableUnits > 0);

            // The editions say which of them have files; a file names its edition and a volume its cover.
            using var editions = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10/editions");
            var list = (await editions.Content.ReadFromJsonAsync<BaseDataList<WorkEditionV1>>())!;
            Assert.AreEqual(2, list.Count);
            Assert.IsTrue(list.Single(edition => edition.Id == 1).IsAvailable);
            Assert.IsFalse(list.Single(edition => edition.Id == 2).IsAvailable);
            using var availability = await host.SendAsync(HttpMethod.Get, "/api/v1/works/10/availability");
            var item = (await availability.Content.ReadFromJsonAsync<PageResult<WorkAvailabilityV1>>())!.Items.Single();
            Assert.AreEqual(1L, item.WorkEditionId);
            Assert.AreEqual("https://example.test/v1.jpg", item.UnitImageUrl);

            // The page: the rating in the hero, the tabs of what exists (no People, none of Related or Similar), each tab at its own address.
            var overview = await GetAsync(host, "/Details/10");
            StringAssert.Contains(overview, "media-hero__rating");
            StringAssert.Contains(overview, "src=\"https://example.test/poster.webp\"");
            StringAssert.Contains(overview, "loading=\"lazy\"");
            Assert.IsFalse(overview.Contains("<picture>", StringComparison.Ordinal));
            StringAssert.Contains(overview, "media-availability-badge--inline");
            StringAssert.Contains(overview, "Open Library");
            StringAssert.Contains(overview, "href=\"/Details/10?tab=volumes\"");
            StringAssert.Contains(overview, "href=\"/Details/10?tab=editions\"");
            Assert.IsFalse(overview.Contains("tab=people", StringComparison.Ordinal));
            Assert.IsFalse(overview.Contains("tab=related", StringComparison.Ordinal));
            var volumes = await GetAsync(host, "/Details/10?tab=volumes");
            StringAssert.Contains(volumes, "details-volume");
            // Every volume that is here has its own read button next to its cover, so none needs a visit first.
            StringAssert.Contains(volumes, "details-volume-item");
            StringAssert.Matches(volumes, new System.Text.RegularExpressions.Regex("details-volume-item.*?href=\"/Read/\\d+\"", System.Text.RegularExpressions.RegexOptions.Singleline));
            Assert.IsFalse(volumes.Contains("details-block__heading", StringComparison.Ordinal));
            StringAssert.Contains(await GetAsync(host, "/Details/10?tab=editions"), "Deutsch EPUB");

            // A tab that does not exist shows the Overview.
            StringAssert.Contains(await GetAsync(host, "/Details/10?tab=people"), "details-block__heading");
        }

        private static async Task<string> GetAsync(Host host, string path)
        {
            using var page = await host.SendAsync(HttpMethod.Get, path);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            return html;
        }
    }
}
