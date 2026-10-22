using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // Provider images kept on disk: which ones are still to fetch, and what a fetch changes.
    internal static class WorkCoverLogic
    {
        // The poster and backdrop facts that still name a provider, then season and episode images; a failed address waits a day.
        public static async Task<IReadOnlyList<WorkCoverTargetData>> ReadPendingCoversAsync(this LogicContext ctx, WorkCoverQueryData query, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    fact."WorkId",
                    CASE fact."FactKey" WHEN 'Poster' THEN 1 ELSE 2 END::smallint AS "CoverKindId",
                    NULL::bigint AS "WorkUnitId",
                    fact."Value" AS "SourceUrl"
                FROM
                    "WorkMetadataFacts" AS fact
                WHERE
                    fact."FactKey" IN ('Poster', 'Backdrop')
                    AND (@WorkId::bigint IS NULL OR fact."WorkId" = @WorkId)
                    AND fact."Value" ~ '^https?://'
                    AND length(fact."Value") <= 500
                    AND fact."Id" = (
                        SELECT
                            effective."Id"
                        FROM
                            "WorkMetadataFacts" AS effective
                        WHERE
                            effective."WorkId" = fact."WorkId"
                            AND effective."FactKey" = fact."FactKey"
                        ORDER BY
                            effective."IsManual" DESC,
                            effective."Id"
                        LIMIT 1
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkCovers" AS cover
                        WHERE
                            cover."WorkId" = fact."WorkId"
                            AND cover."WorkUnitId" IS NULL
                            AND cover."CoverKindId" = CASE fact."FactKey" WHEN 'Poster' THEN 1 ELSE 2 END
                            AND cover."SourceUrl" = fact."Value"
                            AND (cover."StoredAt" IS NOT NULL OR cover."FailedAt" > now() - interval '1 day')
                    )
                ORDER BY
                    fact."WorkId",
                    fact."FactKey"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(query);
            var targets = (await ctx.ReadSql.ReadListAsync<WorkCoverTargetData>(sql, ct)).ToList();
            if (query.WorkId is not null || targets.Count >= query.Limit)
            {
                return targets;
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unit."WorkId",
                    3::smallint AS "CoverKindId",
                    unit."Id" AS "WorkUnitId",
                    unit."ImageUrl" AS "SourceUrl"
                FROM
                    "WorkUnits" AS unit
                WHERE
                    unit."ImageUrl" ~ '^https?://'
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkCovers" AS cover
                        WHERE
                            cover."WorkId" = unit."WorkId"
                            AND cover."WorkUnitId" = unit."Id"
                            AND cover."CoverKindId" = 3
                            AND cover."SourceUrl" = unit."ImageUrl"
                            AND (cover."StoredAt" IS NOT NULL OR cover."FailedAt" > now() - interval '1 day')
                    )
                ORDER BY
                    unit."WorkId",
                    unit."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(new WorkCoverQueryData(null, query.Limit - targets.Count));
            targets.AddRange(await ctx.ReadSql.ReadListAsync<WorkCoverTargetData>(sql, ct));
            return targets;
        }

        // The stored file becomes what the facts and the unit show; the provider's address stays here to recognise an unchanged one.
        public static async Task SaveWorkCoverAsync(this LogicContext ctx, WorkCoverStoredData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH saved AS (
                    INSERT INTO
                        "WorkCovers" ("WorkId", "WorkUnitId", "CoverKindId", "SourceUrl", "ContentType", "Version", "LocalUrl", "StoredAt", "FailedAt", "FormatId")
                    VALUES
                        (@WorkId, @WorkUnitId, @CoverKindId, @SourceUrl, @ContentType, @Version, @LocalUrl, now(), NULL, @FormatId)
                    ON CONFLICT ("WorkId", "CoverKindId", "WorkUnitId") DO UPDATE
                    SET
                        "SourceUrl" = EXCLUDED."SourceUrl",
                        "ContentType" = EXCLUDED."ContentType",
                        "Version" = EXCLUDED."Version",
                        "LocalUrl" = EXCLUDED."LocalUrl",
                        "StoredAt" = now(),
                        "FailedAt" = NULL,
                        "FormatId" = EXCLUDED."FormatId"
                    RETURNING
                        "Id"
                ), facts AS (
                    UPDATE
                        "WorkMetadataFacts" AS fact
                    SET
                        "Value" = @LocalUrl,
                        "UpdatedAt" = now()
                    WHERE
                        @CoverKindId IN (1, 2)
                        AND fact."WorkId" = @WorkId
                        AND fact."FactKey" = CASE @CoverKindId WHEN 1 THEN 'Poster' ELSE 'Backdrop' END
                        AND fact."Value" = @SourceUrl
                    RETURNING
                        fact."Id"
                )
                UPDATE
                    "WorkUnits" AS unit
                SET
                    "ImageUrl" = @LocalUrl
                WHERE
                    @CoverKindId = 3
                    AND unit."Id" = @WorkUnitId
                    AND unit."WorkId" = @WorkId
                    AND unit."ImageUrl" = @SourceUrl;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A failed fetch forgets the older file's address, so it is never shown for the new one.
        public static async Task SaveWorkCoverFailureAsync(this LogicContext ctx, WorkCoverTargetData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkCovers" ("WorkId", "WorkUnitId", "CoverKindId", "SourceUrl", "FailedAt")
                VALUES
                    (@WorkId, @WorkUnitId, @CoverKindId, @SourceUrl, now())
                ON CONFLICT ("WorkId", "CoverKindId", "WorkUnitId") DO UPDATE
                SET
                    "SourceUrl" = EXCLUDED."SourceUrl",
                    "ContentType" = NULL,
                    "Version" = NULL,
                    "LocalUrl" = NULL,
                    "StoredAt" = NULL,
                    "FailedAt" = now(),
                    "FormatId" = NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<CoverCacheSettingsData> ReadCoverCacheSettingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."KeepDays"::integer AS "KeepDays",
                    settings."MaximumMegabytes",
                    settings."FormatId",
                    settings."Version"
                FROM
                    "CoverCacheSettings" AS settings;
                """;
            return ctx.ReadSql.ReadRequiredAsync<CoverCacheSettingsData>(sql, ct);
        }

        // The Owner's choices replace the old ones, if nobody changed them in between.
        public static async Task UpdateCoverCacheSettingsAsync(this LogicContext ctx, CoverCacheSettingsData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "CoverCacheSettings" AS settings
                SET
                    "KeepDays" = @KeepDays,
                    "MaximumMegabytes" = @MaximumMegabytes,
                    "FormatId" = @FormatId,
                    "Version" = settings."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    settings."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }

        // Stored covers whose widths are not kept in this format yet, oldest first; one the backfill has not seen is always among them.
        public static async Task<IReadOnlyList<WorkCoverConvertData>> ReadCoversToConvertAsync(this LogicContext ctx, WorkCoverConvertQueryData query, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    cover."WorkId",
                    cover."CoverKindId",
                    cover."WorkUnitId",
                    cover."ContentType",
                    cover."Version"
                FROM
                    "WorkCovers" AS cover
                WHERE
                    cover."StoredAt" IS NOT NULL
                    AND cover."FormatId" IS DISTINCT FROM @FormatId
                ORDER BY
                    cover."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(query);
            return await ctx.ReadSql.ReadListAsync<WorkCoverConvertData>(sql, ct);
        }

        // The widths were made in this format; a cover that was stored again in the meantime keeps what its own store wrote.
        public static async Task SaveWorkCoverFormatAsync(this LogicContext ctx, WorkCoverFormatData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkCovers" AS cover
                SET
                    "FormatId" = @FormatId
                WHERE
                    cover."WorkId" = @WorkId
                    AND cover."CoverKindId" = @CoverKindId
                    AND cover."WorkUnitId" IS NOT DISTINCT FROM @WorkUnitId
                    AND cover."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Every stored cover of these Works, to find the files nobody refers to.
        public static async Task<IReadOnlyList<WorkCoverRefData>> ReadStoredCoversAsync(this LogicContext ctx, WorkCoverWorkIdsData works, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    cover."WorkId",
                    cover."CoverKindId",
                    cover."WorkUnitId"
                FROM
                    "WorkCovers" AS cover
                WHERE
                    cover."WorkId" = ANY(@WorkIds)
                    AND cover."StoredAt" IS NOT NULL;
                """;
            sql.Parameters.Add(works);
            return await ctx.ReadSql.ReadListAsync<WorkCoverRefData>(sql, ct);
        }
    }
}
