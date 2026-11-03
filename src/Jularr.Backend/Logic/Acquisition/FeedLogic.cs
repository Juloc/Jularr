using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The seen releases of the indexers' recent feeds and the wanted targets a feed makes due.
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
                    ) AS "ExternalIdentities"
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

        // Targets that wait for their next search are searched now, but not sooner than the gap after their last search; one already due, or searched later, stays as it is.
        // Returns the targets that changed.
        public static async Task<IReadOnlyList<WantedItemParameters>> MakeWantedDueAsync(this LogicContext ctx, FeedDueData data, CancellationToken ct)
        {
            var waiting = new SqlStatement();
            waiting.Statement =
                """
                SELECT
                    wanted."Id" AS "WantedItemId"
                FROM
                    "WantedItems" AS wanted
                WHERE
                    wanted."Id" = ANY(@WantedItemIds)
                    AND NOT wanted."IsPaused"
                    AND wanted."NextSearchAt" > greatest(@Now, wanted."LastSearchedAt" + interval '1 minute' * @GapMinutes)
                ORDER BY
                    wanted."Id";
                """;
            waiting.Parameters.Add(data);
            var changing = await ctx.ReadSql.ReadListAsync<WantedItemParameters>(waiting, ct);
            if (changing.Count == 0)
            {
                return changing;
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WantedItems" AS wanted
                SET
                    "NextSearchAt" = greatest(@Now, wanted."LastSearchedAt" + interval '1 minute' * @GapMinutes),
                    "LastOutcomeKey" = @OutcomeKey
                WHERE
                    wanted."Id" = ANY(@WantedItemIds);
                """;
            sql.Parameters.Add(new FeedDueData([.. changing.Select(item => item.WantedItemId)], data.OutcomeKey, data.Now, data.GapMinutes));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return changing;
        }

        // The first moment after now at which a missing episode or movie of the target has had its release day: the search of that day, when the feed found nothing.
        // A release comes out during the day, so the search waits until the day is over (the day's end in UTC and six hours more).
        public static async Task<DateTime?> ReadNextReleaseDayAsync(this LogicContext ctx, WantedReleaseDayData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    min(upcoming."SearchAt") AS "SearchAt"
                FROM
                    (
                        SELECT
                            (unit."ReleasedOn"::timestamp AT TIME ZONE 'UTC') + interval '30 hours' AS "SearchAt"
                        FROM
                            "WantedItems" AS wanted
                            JOIN "WorkUnits" AS unit ON unit."WorkId" = wanted."WorkId"
                        WHERE
                            wanted."Id" = @WantedItemId
                            AND unit."WorkUnitTypeId" = 2
                            AND unit."IsMonitored"
                            AND unit."ReleasedOn" IS NOT NULL
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                WHERE
                                    asset."WorkUnitId" = unit."Id"
                            )
                        UNION ALL
                        SELECT
                            (work."ReleasedOn"::timestamp AT TIME ZONE 'UTC') + interval '30 hours'
                        FROM
                            "WantedItems" AS wanted
                            JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                        WHERE
                            wanted."Id" = @WantedItemId
                            AND work."MediaTypeId" = 1
                            AND work."ReleasedOn" IS NOT NULL
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "MediaAssets" AS asset
                                    JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL
                                WHERE
                                    asset."WorkId" = work."Id"
                            )
                    ) AS upcoming
                WHERE
                    upcoming."SearchAt" > @Now;
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadSql.ExecuteScalarAsync(sql, ct) is DateTime at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;
        }
    }
}
