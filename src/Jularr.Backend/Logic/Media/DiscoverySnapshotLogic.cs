using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class DiscoverySnapshotLogic
    {
        private const int MaximumPayloadBytes = 2 * 1024 * 1024;

        // Freshness is not retention: stale results remain readable until the
        // provider-specific legal deadline, even after application restarts.
        public static Task<DiscoverySnapshotData?> ReadDiscoverySnapshotAsync(this LogicContext ctx, DiscoverySnapshotKeyData key, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    snapshot."Key",
                    snapshot."ProviderKey",
                    snapshot."Payload"::text AS "Payload",
                    snapshot."FetchedAt",
                    snapshot."FreshUntil",
                    snapshot."RetainUntil"
                FROM
                    "DiscoverySnapshots" AS snapshot
                WHERE
                    snapshot."Key" = @Key
                    AND snapshot."RetainUntil" > now();
                """;
            sql.Parameters.Add(key);
            return ctx.ReadSql.ReadOptionalAsync<DiscoverySnapshotData>(sql, ct);
        }

        // Caller supplies one fully validated provider response. One SQL statement
        // replaces payload and generation together; an older concurrent fetch
        // cannot overwrite a newer successful result or change the provider.
        public static async Task<bool> SaveDiscoverySnapshotAsync(this LogicContext ctx, DiscoverySnapshotData data, CancellationToken ct)
        {
            data.Validate();
            if (data.FetchedAt.Kind != DateTimeKind.Utc || data.FreshUntil.Kind != DateTimeKind.Utc || data.RetainUntil.Kind != DateTimeKind.Utc
                || data.FreshUntil < data.FetchedAt || data.RetainUntil <= data.FreshUntil)
            {
                throw new DataValidationException(nameof(data.RetainUntil), "invalid_range");
            }

            if (Encoding.UTF8.GetByteCount(data.Payload) > MaximumPayloadBytes)
            {
                throw new DataValidationException(nameof(data.Payload), "too_long");
            }

            try
            {
                using var document = JsonDocument.Parse(data.Payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                {
                    throw new DataValidationException(nameof(data.Payload), "invalid_value");
                }
            }
            catch (JsonException)
            {
                throw new DataValidationException(nameof(data.Payload), "invalid_value");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "DiscoverySnapshots" (
                    "Key",
                    "ProviderKey",
                    "Payload",
                    "FetchedAt",
                    "FreshUntil",
                    "RetainUntil"
                )
                VALUES (
                    @Key,
                    @ProviderKey,
                    @Payload::jsonb,
                    @FetchedAt,
                    @FreshUntil,
                    @RetainUntil
                )
                ON CONFLICT ("Key") DO UPDATE
                SET
                    "Payload" = EXCLUDED."Payload",
                    "FetchedAt" = EXCLUDED."FetchedAt",
                    "FreshUntil" = EXCLUDED."FreshUntil",
                    "RetainUntil" = EXCLUDED."RetainUntil"
                WHERE
                    "DiscoverySnapshots"."ProviderKey" = EXCLUDED."ProviderKey"
                    AND "DiscoverySnapshots"."FetchedAt" <= EXCLUDED."FetchedAt";
                """;
            sql.Parameters.Add(data);
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1;
        }

        // Legal expiration still wins over last-known-good. This is run by
        // maintenance only, never on every Discover or Hero render.
        public static Task<int> PruneDiscoverySnapshotsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "DiscoverySnapshots"
                WHERE
                    "RetainUntil" <= now();
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
