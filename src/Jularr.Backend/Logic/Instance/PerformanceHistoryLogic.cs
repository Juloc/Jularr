using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Instance
{
    // The last 24 hours of the process's load and of what each service did, kept across a restart.
    internal static class PerformanceHistoryLogic
    {
        public static Task<IReadOnlyList<PerformanceSampleRowData>> ReadPerformanceSamplesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    sample."SampledAt",
                    sample."ProcessorPercent",
                    sample."MemoryBytes"
                FROM
                    "PerformanceSamples" AS sample
                WHERE
                    sample."SampledAt" >= now() - interval '24 hours'
                ORDER BY
                    sample."SampledAt";
                """;
            return ctx.ReadSql.ReadListAsync<PerformanceSampleRowData>(sql, ct);
        }

        public static Task<IReadOnlyList<ServiceTimingRowData>> ReadServiceTimingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    timing."Service",
                    timing."SliceStartedAt",
                    timing."Calls",
                    timing."Errors",
                    timing."TotalMilliseconds",
                    timing."MaximumMilliseconds"
                FROM
                    "ServiceTimings" AS timing
                WHERE
                    timing."SliceStartedAt" >= now() - interval '24 hours'
                ORDER BY
                    timing."SliceStartedAt",
                    timing."Service";
                """;
            return ctx.ReadSql.ReadListAsync<ServiceTimingRowData>(sql, ct);
        }

        // New samples are added, a slice that grew replaces its row, and whatever is older than 24 hours leaves.
        public static async Task SavePerformanceHistoryAsync(this LogicContext ctx, PerformanceSaveData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH old_samples AS (
                    DELETE FROM
                        "PerformanceSamples"
                    WHERE
                        "SampledAt" < now() - interval '24 hours'
                    RETURNING
                        1
                ), old_timings AS (
                    DELETE FROM
                        "ServiceTimings"
                    WHERE
                        "SliceStartedAt" < now() - interval '24 hours'
                    RETURNING
                        1
                ), new_samples AS (
                    INSERT INTO
                        "PerformanceSamples" ("SampledAt", "ProcessorPercent", "MemoryBytes")
                    SELECT
                        to_timestamp(sample.seconds::double precision),
                        sample.tenths / 10.0,
                        sample.memory
                    FROM
                        unnest(@SampleSeconds, @SampleTenths, @SampleMemory) AS sample (seconds, tenths, memory)
                    ON CONFLICT ("SampledAt") DO NOTHING
                    RETURNING
                        1
                )
                INSERT INTO
                    "ServiceTimings" ("Service", "SliceStartedAt", "Calls", "Errors", "TotalMilliseconds", "MaximumMilliseconds")
                SELECT
                    slice.service,
                    to_timestamp(slice.seconds::double precision),
                    slice.calls,
                    slice.errors,
                    slice.total,
                    slice.maximum
                FROM
                    unnest(@Services, @SliceSeconds, @Calls, @Errors, @TotalMilliseconds, @MaximumMilliseconds) AS slice (service, seconds, calls, errors, total, maximum)
                ON CONFLICT ("Service", "SliceStartedAt") DO UPDATE
                SET
                    "Calls" = EXCLUDED."Calls",
                    "Errors" = EXCLUDED."Errors",
                    "TotalMilliseconds" = EXCLUDED."TotalMilliseconds",
                    "MaximumMilliseconds" = EXCLUDED."MaximumMilliseconds";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
