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
    // The daily cleanup of catalog titles (#1651 step 4): details of unused titles go, then the rows of providers that keep no whole catalog.
    [TestClass]
    public sealed class CatalogCleanupTests
    {
        private const string Old = "now() - interval '30 days'";

        [TestMethod]
        public async Task Cleanup_KeepsEveryProtectedTitle_DropsOnlyDetailsOfUnusedOnes_AndDeletesUnusedRowsOfLeanProviders()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Films", MediaType.Movie);
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorkSyncs\" SET \"CacheTier\" = 3 WHERE \"Namespace\" = 'anilist'; UPDATE \"ExternalWorkSyncs\" SET \"CacheTier\" = 1 WHERE \"Namespace\" = 'tmdb_tv';");

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

            // Minimal rows: 30 old lean row, 31 in a feed, 32 relation target of a Work with details, 33 tier 3, 34 recent, 35 favorite, 36 tier 1, 37 trending, 38 target of an unused Work.
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "FetchedAt", "TrendingRank")
                VALUES (30, 1, 'Lean old', 'tmdb_movie', {Old}, NULL), (31, 1, 'In a feed', 'tmdb_movie', {Old}, NULL), (32, 1, 'Related', 'tmdb_movie', {Old}, NULL),
                    (33, 2, 'Whole catalog', 'anilist', {Old}, NULL), (34, 1, 'Recent', 'tmdb_movie', now(), NULL), (35, 1, 'Favorite', 'tmdb_movie', {Old}, NULL),
                    (36, 2, 'Tier one', 'tmdb_tv', {Old}, NULL), (37, 1, 'Trending', 'tmdb_movie', {Old}, 2), (38, 1, 'Target of an unused', 'tmdb_movie', {Old}, NULL);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT id, CASE WHEN id = 33 THEN 'anilist' WHEN id = 36 THEN 'tmdb_tv' ELSE 'tmdb_movie' END, 'm' || id FROM unnest(ARRAY[30,31,32,33,34,35,36,37,38]) AS id;
                INSERT INTO "ExternalWorkFeedItems" ("FeedId", "Position", "WorkId") VALUES ((SELECT "Id" FROM "ExternalWorkFeeds" WHERE "Key" = 'top'), 2, 31);
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (22, 32, 1, 'tmdb'), (20, 38, 1, 'tmdb');
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT (SELECT min("Id") FROM "Profiles"), 35;
                -- Work 20 belongs to the whole catalog (tier 3): it keeps its row once its details are gone; on tier 2 the row would go as well.
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

            // On tier 2 the same Work would be deleted in the same pass: Work 24 only stays because a feed lists it.
            // Trending (rank or trending feed) and recently opened titles keep their details; a title only in another feed does not.
            foreach (var id in new[] { 21, 22, 23 })
            {
                Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"Works\" WHERE \"Id\" = {id} AND \"DetailedAt\" IS NOT NULL"), "Work " + id);
            }
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"Id\" = 24 AND \"DetailedAt\" IS NULL"));

            // Lean providers lose unused minimal rows; a feed, a relation target of a Work with details, tier 3, a recent row, a tie and a trending rank keep them.
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"Id\" IN (30, 36, 38)"), "Unused minimal rows of tier 1 and 2 are deleted.");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"WorkId\" IN (30, 36, 38)"));
            foreach (var (id, reason) in new[] { (31, "In a feed"), (32, "Target of a Work with details"), (33, "Tier 3 keeps the whole catalog"), (34, "Used recently"), (35, "Favorite"), (37, "Trending") })
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
    }
}
