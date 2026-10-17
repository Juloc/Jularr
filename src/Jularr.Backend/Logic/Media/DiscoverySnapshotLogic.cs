using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class DiscoverySnapshotLogic
    {
        public static Task<DiscoveryCachePolicyData> ReadDiscoveryCachePolicyAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = """
                SELECT
                    "MetadataTrendingHours",
                    "MetadataRatingsHours",
                    "MetadataFreshDays",
                    "DiscoveryCacheEntries",
                    "DiscoveryCacheMegabytes"
                FROM
                    "InstanceSettings"
                WHERE
                    "Id" = 1;
                """;
            return ctx.ReadSql.ReadRequiredAsync<DiscoveryCachePolicyData>(sql, ct);
        }

        public static Task<DiscoverySnapshotData?> ReadDiscoverySnapshotAsync(this LogicContext ctx, DiscoverySnapshotKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = """
                WITH touched AS (
                    UPDATE
                        "DiscoverySnapshots"
                    SET
                        "LastUsedAt" = now()
                    WHERE
                        "Key" = @Key
                        AND "LastUsedAt" < now() - interval '1 hour'
                )
                SELECT
                    "Key",
                    "Request"::text AS "Request",
                    "Payload"::text AS "Payload",
                    "RefreshAfter" > now() AS "IsFresh"
                FROM
                    "DiscoverySnapshots"
                WHERE
                    "Key" = @Key;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadOptionalAsync<DiscoverySnapshotData>(sql, ct);
        }

        public static Task<int> SaveDiscoverySnapshotAsync(this LogicContext ctx, DiscoverySnapshotWriteData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = """
                -- Serialise budget enforcement across different provider keys, including multiple hosts.
                SELECT pg_advisory_xact_lock(70461017);
                INSERT INTO "DiscoverySnapshots" ("Key", "Request", "Payload", "RefreshAfter", "RetryAfter")
                SELECT
                    @Key,
                    @Request::jsonb,
                    @Payload::jsonb,
                    now() + make_interval(hours => @FreshHours),
                    now() + make_interval(hours => @FreshHours)
                WHERE
                    octet_length(@Payload::jsonb::text) <= 1048576
                    AND octet_length(@Request::jsonb::text) <= 4096
                ON CONFLICT ("Key") DO UPDATE
                SET
                    "Payload" = EXCLUDED."Payload",
                    "RefreshAfter" = EXCLUDED."RefreshAfter",
                    "RetryAfter" = EXCLUDED."RetryAfter";

                -- Cache eviction never deletes Works or facts. The configured count and byte budgets both apply.
                DELETE FROM
                    "DiscoverySnapshots"
                WHERE "Key" IN (
                    SELECT
                        "Key"
                    FROM (
                        SELECT
                            "Key",
                            row_number() OVER recent AS position,
                            sum(octet_length("Payload"::text) + octet_length("Request"::text)) OVER recent AS bytes
                        FROM
                            "DiscoverySnapshots"
                        WINDOW recent AS (ORDER BY "LastUsedAt" DESC, "Key")
                    ) AS budget
                    WHERE
                        position > @DiscoveryCacheEntries
                        OR bytes > @DiscoveryCacheMegabytes::bigint * 1048576
                );
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<DiscoverySnapshotData?> ClaimDiscoverySnapshotAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = """
                WITH due AS (
                    SELECT
                        "Key"
                    FROM
                        "DiscoverySnapshots"
                    WHERE
                        "RefreshAfter" <= now()
                        AND "RetryAfter" <= now()
                        AND "LastUsedAt" > now() - interval '30 days'
                    ORDER BY
                        "RetryAfter",
                        "Key"
                    LIMIT 1 FOR UPDATE SKIP LOCKED
                )
                UPDATE
                    "DiscoverySnapshots" AS snapshot
                SET
                    "RetryAfter" = now() + interval '15 minutes'
                FROM
                    due
                WHERE
                    due."Key" = snapshot."Key"
                RETURNING
                    snapshot."Key",
                    snapshot."Request"::text AS "Request",
                    snapshot."Payload"::text AS "Payload",
                    FALSE AS "IsFresh";
                """;
            return ctx.ReadOptionalAsync<DiscoverySnapshotData>(sql, ct);
        }
    }
}
