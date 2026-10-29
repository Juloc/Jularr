using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // Keeps the catalog small after the instance's caching tier: details nobody uses go first, then the catalog rows of catalogs that are not kept whole.
    // A title is used when something ties it to the library (WorkLibraryRelations), a file record exists, or it is trending.
    internal static class CatalogCleanupLogic
    {
        // The tier as the cleanup reads it, from the one rule in ExternalWorkLogic.
        public static CatalogCleanupData CleanupFor(CachingTier tier, int limit) =>
            new(limit, tier != CachingTier.Lean, tier == CachingTier.Full ? [.. ExternalWorkLogic.FullSyncCatalogs] : []);

        // Drops the details (child rows) of titles without a library tie that nobody opened for CatalogDetailKeepDays and that are not trending. While Discover keeps
        // its lists, the titles of its trending lists keep them too, and with DetailsDiscoverAhead every title of a Discover list. The minimal row, its identities and
        // incoming relations stay. A title without an identity a provider can read again keeps its details.
        // Returns how many titles were reset; the caller repeats while a statement fills its limit.
        public static async Task<int> ResetUnusedCatalogDetailsAsync(this LogicContext ctx, CatalogCleanupData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH due AS (
                    SELECT
                        work."Id"
                    FROM
                        "Works" AS work
                    WHERE
                        work."DetailedAt" IS NOT NULL
                        AND work."TrendingRank" IS NULL
                        AND coalesce(work."ViewedAt", '-infinity'::timestamptz) < now() - make_interval(days => (SELECT settings."CatalogDetailKeepDays"::integer FROM "InstanceSettings" AS settings))
                        AND NOT EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id")
                        AND NOT EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkId" = work."Id")
                        AND EXISTS (SELECT 1 FROM "WorkExternalIdentities" AS identity WHERE identity."WorkId" = work."Id" AND identity."Namespace" <> 'plex')
                        AND NOT (@KeepsDiscoverLists AND EXISTS (
                            SELECT 1
                            FROM
                                "ExternalWorkFeedItems" AS item
                                JOIN "ExternalWorkFeeds" AS feed ON feed."Id" = item."FeedId" AND feed."SourceValue" IS NULL
                                CROSS JOIN "InstanceSettings" AS settings
                            WHERE
                                item."WorkId" = work."Id"
                                AND settings."Id" = 1
                                AND (feed."DiscoverFeedTypeId" = 1 OR settings."DetailsDiscoverAhead")))
                    ORDER BY
                        work."Id"
                    LIMIT @Limit
                    FOR UPDATE OF work SKIP LOCKED
                ), titles AS (
                    DELETE FROM "WorkTitles" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), facts AS (
                    DELETE FROM "WorkMetadataFacts" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), covers AS (
                    DELETE FROM "WorkCovers" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), units AS (
                    DELETE FROM "WorkUnits" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), unit_groups AS (
                    DELETE FROM "WorkUnitGroups" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), editions AS (
                    DELETE FROM "WorkEditions" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), people AS (
                    DELETE FROM "WorkPeople" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), platforms AS (
                    DELETE FROM "WorkPlatforms" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), tags AS (
                    DELETE FROM "WorkTags" WHERE "WorkId" IN (SELECT "Id" FROM due)
                ), relations AS (
                    DELETE FROM "WorkRelations" WHERE "WorkId" IN (SELECT "Id" FROM due)
                )
                UPDATE
                    "Works" AS work
                SET
                    "DetailedAt" = NULL
                FROM
                    due
                WHERE
                    work."Id" = due."Id";
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Deletes catalog rows without details of catalogs that are not kept whole that nothing uses for CatalogDetailKeepDays:
        // no library tie, no feed, no trending rank, and not the target of a relation of a title with details or a library tie.
        // A catalog counts with its sync settings; TheTVDB's series, those left of TMDB's series catalog and Narō's searched web novels count without.
        public static async Task<int> DeleteUnusedCatalogTitlesAsync(this LogicContext ctx, CatalogCleanupData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH due AS (
                    SELECT
                        work."Id"
                    FROM
                        "Works" AS work
                    WHERE
                        (work."CatalogNamespace" IN ('tvdb', 'tmdb_tv', 'syosetu') OR EXISTS (SELECT 1 FROM "ExternalWorkSyncs" AS sync WHERE sync."Namespace" = work."CatalogNamespace"))
                        AND work."CatalogNamespace" <> ALL(@WholeCatalogs)
                        AND work."DetailedAt" IS NULL
                        AND work."TrendingRank" IS NULL
                        AND coalesce(work."ViewedAt", work."FetchedAt", work."CreatedAt") < now() - make_interval(days => (SELECT settings."CatalogDetailKeepDays"::integer FROM "InstanceSettings" AS settings))
                        AND NOT EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id")
                        AND NOT EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkId" = work."Id")
                        AND NOT EXISTS (SELECT 1 FROM "ExternalWorkFeedItems" AS item WHERE item."WorkId" = work."Id")
                        AND NOT EXISTS (
                            SELECT 1
                            FROM
                                "WorkRelations" AS relation
                                JOIN "Works" AS source ON source."Id" = relation."WorkId"
                            WHERE
                                relation."RelatedWorkId" = work."Id"
                                AND (source."DetailedAt" IS NOT NULL OR EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = source."Id")))
                    ORDER BY
                        work."Id"
                    LIMIT @Limit
                    FOR UPDATE OF work SKIP LOCKED
                )
                DELETE FROM
                    "Works" AS work
                USING
                    due
                WHERE
                    work."Id" = due."Id";
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
