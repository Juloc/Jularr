using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.System.Media.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    // The daily cleanup of catalog titles (#1651 step 4): details of unused titles go, then the rows of catalogs the Caching module does not keep whole.
    [TestClass]
    public sealed class CatalogCleanupTests
    {
        private const string Old = "now() - interval '30 days'";

        [TestMethod]
        public async Task Cleanup_KeepsEveryProtectedTitle_DropsOnlyDetailsOfUnusedOnes_AndDeletesUnusedRowsOfCatalogsNotKeptWhole()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Films", MediaType.Movie);
            // The full catalog keeps TMDB and AniList whole; Open Library has no full list and keeps Discover's lists only.
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('DiscoverCache', 'LocalDiscovery');
                INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute") VALUES ('openlibrary', 20);
                """);

            // Details: every one of these has an old ViewedAt, a title, a fact, a unit and a cover, so only a protection keeps it.
            var ids = "ARRAY[1,2,3,4,5,6,7,8,20,21,22,23,24]";
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "FetchedAt", "DetailedAt", "ViewedAt", "TrendingRank")
                SELECT id, 1, 'Title ' || id, 'tmdb_movie', {Old}, {Old}, {Old}, CASE WHEN id = 21 THEN 1 END FROM unnest({ids}) AS id;
                UPDATE "Works" SET "ViewedAt" = now() WHERE "Id" = 22;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT id, 'tmdb_movie', 'm' || id FROM unnest({ids}) AS id;
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "ProviderId", "IsManual") SELECT id, 'Overview', 'Text', (SELECT min("Id") FROM "Providers"), id = 8 FROM unnest({ids}) AS id;
                INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") SELECT id, 2, 1 FROM unnest({ids}) AS id;
                INSERT INTO "WorkCovers" ("WorkId", "CoverKindId", "SourceUrl") SELECT id, 1, 'https://image.test/' || id FROM unnest({ids}) AS id;
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual", "ProviderId") SELECT id, 'Alt ' || id, FALSE, (SELECT min("Id") FROM "Providers") FROM unnest({ids}) AS id;

                -- The protections: files, request, wanted, favorite, collection, progress, an AniList list entry, a manual edit (work 8 above).
                WITH edition AS (INSERT INTO "WorkEditions" ("WorkId") VALUES (1) RETURNING "Id", "WorkId"),
                version AS (INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") SELECT "WorkId", "Id" FROM edition RETURNING "Id", "WorkId"),
                asset AS (INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") SELECT "WorkId", "Id", 1 FROM version RETURNING "Id")
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId") SELECT {rootId}, 'Title 1.mkv', 1, now(), now(), "Id" FROM asset;
                INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByAccountId", "UiLocaleId") SELECT 2, (SELECT min("Id") FROM "Accounts"), (SELECT min("Id") FROM "UiLocales");
                INSERT INTO "WantedItems" ("WorkId") VALUES (3);
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT (SELECT min("Id") FROM "Profiles"), 4;
                WITH list AS (INSERT INTO "Collections" ("ProfileId", "Name") SELECT (SELECT min("Id") FROM "Profiles"), 'Mine' RETURNING "Id")
                INSERT INTO "CollectionWorks" ("CollectionId", "WorkId", "Position") SELECT "Id", 5, 1 FROM list;
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "Locator", "Progression")
                SELECT (SELECT min("Id") FROM "Profiles"), 6, (SELECT "Id" FROM "WorkUnits" WHERE "WorkId" = 6), 2, 'p', 0.5;
                INSERT INTO "AniListConnections" ("ProfileId", "ViewerId", "ViewerName", "ProtectedToken") SELECT (SELECT min("Id") FROM "Profiles"), 7, 'viewer', '\x00'::bytea;
                INSERT INTO "AniListEntries" ("ProfileId", "MediaId", "ListEntryId", "IsManga", "Title", "Status", "Progress", "RemoteUpdatedAt") SELECT (SELECT min("Id") FROM "Profiles"), 7007, 1, FALSE, 'Entry', 'PLANNING', 0, now();
                UPDATE "WorkExternalIdentities" SET "Namespace" = 'anilist', "Value" = '7007' WHERE "WorkId" = 7;

                -- Work 23 is in a trending feed (its rank was lost); work 24 only in a feed that is not trending.
                INSERT INTO "ExternalWorkFeeds" ("Key", "Namespace", "MediaTypeId", "DiscoverFeedTypeId", "Page") VALUES ('trending', 'tmdb_movie', 1, 1, 1), ('top', 'tmdb_movie', 1, 2, 1);
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId") VALUES ((SELECT "Id" FROM "ExternalWorkFeeds" WHERE "Key" = 'trending'), 1, 23), ((SELECT "Id" FROM "ExternalWorkFeeds" WHERE "Key" = 'top'), 1, 24);
                """);

            // Minimal rows of Open Library: 30 old, 31 in a feed, 32 relation target of a Work with details, 34 recent, 35 favorite, 36 old, 37 trending, 38 target of an unused Work;
            // 33 of AniList, kept whole.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "FetchedAt", "TrendingRank")
                VALUES (30, 1, 'Lean old', 'tmdb_movie', {Old}, NULL), (31, 1, 'In a feed', 'tmdb_movie', {Old}, NULL), (32, 1, 'Related', 'tmdb_movie', {Old}, NULL),
                    (33, 2, 'Whole catalog', 'anilist', {Old}, NULL), (34, 1, 'Recent', 'tmdb_movie', now(), NULL), (35, 1, 'Favorite', 'tmdb_movie', {Old}, NULL),
                    (36, 2, 'Tier one', 'tmdb_tv', {Old}, NULL), (37, 1, 'Trending', 'tmdb_movie', {Old}, 2), (38, 1, 'Target of an unused', 'tmdb_movie', {Old}, NULL);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT id, CASE WHEN id = 33 THEN 'anilist' WHEN id = 36 THEN 'tmdb_tv' ELSE 'tmdb_movie' END, 'm' || id FROM unnest(ARRAY[30,31,32,33,34,35,36,37,38]) AS id;
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId") VALUES ((SELECT "Id" FROM "ExternalWorkFeeds" WHERE "Key" = 'top'), 2, 31);
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (22, 32, 1, 'tmdb'), (20, 38, 1, 'tmdb');
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT (SELECT min("Id") FROM "Profiles"), 35;
                UPDATE "Works" SET "CatalogNamespace" = 'openlibrary' WHERE "Id" IN (30, 31, 32, 34, 35, 36, 37, 38);
                -- Work 20 belongs to a catalog kept whole: it keeps its row once its details are gone; without the full catalog the row would go as well.
                UPDATE "Works" SET "CatalogNamespace" = 'anilist' WHERE "Id" = 20;
                """);

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<CleanCatalogTitlesV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default);
                Assert.IsTrue(result.Success);
            }

            // Protected titles keep their details, one case for each of the eight protections.
            foreach (var id in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
            {
                Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"Works\" WHERE \"Id\" = {id} AND \"DetailedAt\" IS NOT NULL"), "Protected Work " + id);
                Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"WorkUnits\" WHERE \"WorkId\" = {id}"), "Units of " + id);
                Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"WorkCovers\" WHERE \"WorkId\" = {id}"), "Covers of " + id);
            }

            // An unused, old Work loses only its details; the row, its identity and its catalog columns stay.
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"Id\" = 20 AND \"DetailedAt\" IS NULL AND \"CatalogNamespace\" = 'anilist' AND \"FetchedAt\" IS NOT NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"WorkId\" = 20 AND \"Value\" = 'm20'"));
            foreach (var table in new[] { "WorkMetadataFacts", "WorkUnits", "WorkCovers", "WorkTitles", "WorkRelations" })
            {
                Assert.AreEqual(0L, await host.ScalarAsync($"SELECT count(*) FROM \"{table}\" WHERE \"WorkId\" = 20"), table);
            }

            // Without the full catalog the same Work would be deleted in the same pass: Work 24 only stays because a feed lists it.
            // Trending (rank or trending feed) and recently opened titles keep their details; a title only in another feed does not.
            foreach (var id in new[] { 21, 22, 23 })
            {
                Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"Works\" WHERE \"Id\" = {id} AND \"DetailedAt\" IS NOT NULL"), "Work " + id);
            }
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"Id\" = 24 AND \"DetailedAt\" IS NULL"));

            // A catalog not kept whole loses unused minimal rows; a feed, a relation target of a Work with details, a whole catalog, a recent row, a tie and a trending rank keep them.
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"Id\" IN (30, 36, 38)"), "Unused minimal rows of a catalog not kept whole are deleted.");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"WorkId\" IN (30, 36, 38)"));
            foreach (var (id, reason) in new[] { (31, "In a feed"), (32, "Target of a Work with details"), (33, "The full catalog keeps AniList whole"), (34, "Used recently"), (35, "Favorite"), (37, "Trending") })
            {
                Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"Works\" WHERE \"Id\" = {id}"), reason);
            }

            // A second pass has nothing left to do.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var again = await scope.ServiceProvider.GetRequiredService<CleanCatalogTitlesV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default);
                Assert.IsFalse(again.Success);
            }

            // The Admin list shows titles with a tie to the library only; the System page counts catalog titles apart.
            using var list = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/works?pageSize=100");
            Assert.AreEqual(HttpStatusCode.OK, list.StatusCode, await list.Content.ReadAsStringAsync());
            var listed = (await list.Content.ReadFromJsonAsync<PageResult<Jularr.Backend.Data.Admin.Media.V1.WorkListItemV1>>())!.Items.Select(item => item.Id).Order().ToArray();
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 35 }, listed);
            using var system = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/system");
            var health = (await system.Content.ReadFromJsonAsync<SystemHealthV1>())!;
            Assert.AreEqual(10L, health.CatalogWorks, "Works 20 to 24, 31, 32, 33, 34 and 37 are catalog only.");
            Assert.AreEqual(1L, health.Library.Single(item => item.MediaTypeId == MediaType.Movie).Count);
        }

        [TestMethod]
        public async Task Cleanup_WhileDiscoverIsDetailedAhead_KeepsTheDetailsOfEveryDiscoverListTitle_AndNoneWithoutCaching()
        {
            await using var host = await AuthenticationTests.Host.CreateAsync(library: true);

            // Old, unused titles with details: one in a top list, one only in a Work's recommendations, one in no list.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "FetchedAt", "DetailedAt", "ViewedAt")
                SELECT id, 1, 'Title ' || id, 'tmdb_movie', {Old}, {Old}, {Old} FROM unnest(ARRAY[40, 41, 42]) AS id;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT id, 'tmdb_movie', 'm' || id FROM unnest(ARRAY[40, 41, 42]) AS id;
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual", "ProviderId") SELECT id, 'Alt ' || id, FALSE, (SELECT min("Id") FROM "Providers") FROM unnest(ARRAY[40, 41, 42]) AS id;
                INSERT INTO "ExternalWorkFeeds" ("Key", "Namespace", "MediaTypeId", "DiscoverFeedTypeId", "Page", "SourceValue") VALUES ('top', 'tmdb_movie', 1, 2, 1, NULL), ('similar', 'tmdb_movie', 1, NULL, 1, '603');
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId") VALUES ((SELECT "Id" FROM "ExternalWorkFeeds" WHERE "Key" = 'top'), 1, 40), ((SELECT "Id" FROM "ExternalWorkFeeds" WHERE "Key" = 'similar'), 1, 41);
                UPDATE "InstanceSettings" SET "DetailsDiscoverAhead" = TRUE;
                """);

            async Task CleanAsync()
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CleanCatalogTitlesV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default);
            }

            const string Detailed = """
                SELECT string_agg(work."Id" || ':' || (work."DetailedAt" IS NOT NULL AND EXISTS (SELECT 1 FROM "WorkTitles" AS title WHERE title."WorkId" = work."Id")), ',' ORDER BY work."Id")
                FROM "Works" AS work WHERE work."Id" IN (40, 41, 42)
                """;
            await CleanAsync();
            Assert.AreEqual("40:true,41:false", await host.ScalarAsync(Detailed),
                "A title of a Discover list keeps its details; recommendations do not, and a title in no list goes with its row (the catalog is not kept whole).");

            // Without the Caching module Discover keeps no lists, so they protect nothing.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" IN ('DiscoverCache', 'LocalDiscovery')");
            await CleanAsync();
            Assert.AreEqual("40:false", await host.ScalarAsync(Detailed.Replace("(40, 41, 42)", "(40)", StringComparison.Ordinal)));
        }
    }
}
