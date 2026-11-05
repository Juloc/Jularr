using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The seen releases of the indexers' recent feeds and the wanted titles a feed's releases are judged for.
    internal static class FeedLogic
    {
        public static Task<FeedSeenData> ReadSeenFeedReleasesAsync(this LogicContext ctx, FeedSeenQueryData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "FeedReleases" AS seen
                        WHERE
                            seen."AcquisitionConnectionId" = @AcquisitionConnectionId
                    ) AS "HasSeen",
                    ARRAY(
                        SELECT
                            seen."ReleaseKey"
                        FROM
                            "FeedReleases" AS seen
                        WHERE
                            seen."AcquisitionConnectionId" = @AcquisitionConnectionId
                            AND seen."ReleaseKey" = ANY(@ReleaseKeys)
                    ) AS "ReleaseKeys";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadRequiredAsync<FeedSeenData>(sql, ct);
        }

        public static async Task RecordFeedReleasesAsync(this LogicContext ctx, FeedSeenQueryData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "FeedReleases" (
                    "AcquisitionConnectionId",
                    "ReleaseKey"
                )
                SELECT
                    @AcquisitionConnectionId,
                    seen_key
                FROM
                    unnest(@ReleaseKeys::text[]) AS seen_key
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Seen releases leave after their retention; the feed shows only the newest ones.
        public static async Task DeleteOldFeedReleasesAsync(this LogicContext ctx, FeedRetentionData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "FeedReleases" AS seen
                WHERE
                    seen."SeenAt" < @SeenBefore;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<IReadOnlyList<FeedTargetData>> ReadFeedTargetsAsync(this LogicContext ctx, FeedTargetQueryData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    ARRAY(
                        SELECT DISTINCT
                            title."Title"
                        FROM
                            "WorkTitles" AS title
                        WHERE
                            title."WorkId" = work."Id"
                            AND title."Title" <> work."CanonicalTitle"
                        ORDER BY
                            title."Title"
                    ) AS "Titles",
                    ARRAY(
                        SELECT
                            identity."Namespace" || ':' || identity."Value"
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                    ) AS "ExternalIdentities",
                    -- Manga and light novel releases seldom name their author.
                    CASE WHEN work."MediaSubtypeId" IS NULL THEN (
                        SELECT
                            creator."Creator"
                        FROM
                            "WorkCreators" AS creator
                        WHERE
                            creator."WorkId" = work."Id"
                    ) END AS "Author"
                FROM
                    "WantedItems" AS wanted
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    wanted."Id" = ANY(@WantedItemIds)
                ORDER BY
                    wanted."Id";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<FeedTargetData>(sql, ct);
        }
    }
}
