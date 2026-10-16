using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Reading
{
    internal static class ReadingLogic
    {
        // Recently read books are never evicted.
        public static readonly TimeSpan ProtectedFor = TimeSpan.FromDays(14);

        public static Task<ReadingDocumentStateData> CreateReadingDocumentAsync(this LogicContext ctx, ReadingDocumentSaveData data, CancellationToken ct)
        {
            // Equal content shares one document; the latest file is remembered as its source.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingDocuments" ("StoredFileId", "SourceSizeBytes", "SourceModifiedAt", "ContentHash", "AnalyzerVersion", "ReadingFormatTypeId", "Title", "Language", "Structure",
                        "TotalCharacters")
                VALUES
                    (@StoredFileId, @SourceSizeBytes, @SourceModifiedAt, @ContentHash, @AnalyzerVersion, @ReadingFormatTypeId, @Title, @Language, @Structure::jsonb, @TotalCharacters)
                ON CONFLICT ("ContentHash", "AnalyzerVersion") DO UPDATE
                SET
                    "StoredFileId" = EXCLUDED."StoredFileId",
                    "SourceSizeBytes" = EXCLUDED."SourceSizeBytes",
                    "SourceModifiedAt" = EXCLUDED."SourceModifiedAt",
                    "LastReadAt" = now()
                RETURNING
                    "Id" AS "DocumentId",
                    "CacheCompletedAt" IS NOT NULL AS "IsCached";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadRequiredAsync<ReadingDocumentStateData>(sql, ct);
        }

        public static async Task ClearReadingResourcesAsync(this LogicContext ctx, ReadingDocumentIdData data, CancellationToken ct)
        {
            // An interrupted preparation starts over.
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ReadingResources" AS resource
                WHERE
                    resource."ReadingDocumentId" = @DocumentId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveReadingResourceAsync(this LogicContext ctx, ReadingResourceData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
                VALUES
                    (@DocumentId, @Path, @MediaType, @Bytes)
                ON CONFLICT ("ReadingDocumentId", "Path") DO UPDATE
                SET
                    "MediaType" = EXCLUDED."MediaType",
                    "Bytes" = EXCLUDED."Bytes";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task CompleteReadingDocumentAsync(this LogicContext ctx, ReadingDocumentCompleteData data, CancellationToken ct)
        {
            // Only a complete package counts as readable without the storage.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReadingDocuments" AS document
                SET
                    "Title" = @Title,
                    "Language" = @Language,
                    "Structure" = @Structure::jsonb,
                    "TotalCharacters" = @TotalCharacters,
                    "CacheBytes" = (
                        SELECT
                            coalesce(sum(length(resource."Bytes")), 0)
                        FROM
                            "ReadingResources" AS resource
                        WHERE
                            resource."ReadingDocumentId" = document."Id"
                    ),
                    "CacheCompletedAt" = now(),
                    "LastReadAt" = now()
                WHERE
                    document."Id" = @DocumentId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task TouchReadingDocumentAsync(this LogicContext ctx, ReadingDocumentIdData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReadingDocuments" AS document
                SET
                    "LastReadAt" = now()
                WHERE
                    document."Id" = @DocumentId
                    AND document."LastReadAt" < now() - interval '1 minute';
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<int> EvictReadingCacheAsync(this LogicContext ctx, ReadingCacheBudgetData data, CancellationToken ct)
        {
            // Newest reads keep their place; older ones beyond the bytes or works budget leave the cache.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH ranked AS (
                    SELECT
                        document."Id",
                        document."LastReadAt",
                        sum(document."CacheBytes") OVER (ORDER BY document."LastReadAt" DESC, document."Id" DESC) AS "RunningBytes",
                        row_number() OVER (ORDER BY document."LastReadAt" DESC, document."Id" DESC) AS "Position"
                    FROM
                        "ReadingDocuments" AS document
                    WHERE
                        document."CacheBytes" > 0
                ), evicted AS (
                    UPDATE
                        "ReadingDocuments" AS document
                    SET
                        "CacheBytes" = 0,
                        "CacheCompletedAt" = NULL
                    FROM
                        ranked
                    WHERE
                        document."Id" = ranked."Id"
                        AND ranked."LastReadAt" < @ProtectedSince
                        AND (ranked."RunningBytes" > @CacheBudgetBytes OR ranked."Position" > @CacheBudgetWorks)
                    RETURNING
                        document."Id"
                ), removed AS (
                    DELETE FROM
                        "ReadingResources" AS resource
                    USING
                        evicted
                    WHERE
                        resource."ReadingDocumentId" = evicted."Id"
                )
                SELECT
                    count(*)::integer AS "Count"
                FROM
                    evicted;
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<ReadingEvictionData>(sql, ct)).Count;
        }

        // The choice belongs to the book the asset is part of, if the profile may open it.
        public static async Task SaveReadingLanguageAsync(this LogicContext ctx, ReadingLanguageData data, CancellationToken ct)
        {
            if (data.ReadingLanguage is { } language && language != "original" && !System.Text.RegularExpressions.Regex.IsMatch(language, "^[a-z]{2,3}$"))
            {
                throw new DataValidationException(nameof(data.ReadingLanguage), "invalid_value");
            }

            var sql = new SqlStatement();
            sql.Statement = data.ReadingLanguage is null
                ? """
                  DELETE FROM
                      "ReadingLanguages" AS chosen
                  USING
                      "MediaAssets" AS asset
                  WHERE
                      asset."Id" = @MediaAssetId
                      AND chosen."ProfileId" = @ProfileId
                      AND chosen."WorkId" = asset."WorkId";
                  """
                : """
                  INSERT INTO
                      "ReadingLanguages" ("ProfileId", "WorkId", "Language")
                  SELECT
                      @ProfileId,
                      asset."WorkId",
                      @ReadingLanguage
                  FROM
                      "MediaAssets" AS asset
                      JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                  WHERE
                      asset."Id" = @MediaAssetId
                  ON CONFLICT ("ProfileId", "WorkId") DO UPDATE
                  SET
                      "Language" = EXCLUDED."Language",
                      "UpdatedAt" = now();
                  """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0 && data.ReadingLanguage is not null)
            {
                throw new KeyNotFoundException("The book was not found.");
            }
        }

        public static async Task SaveReadingProgressAsync(this LogicContext ctx, ReadingProgressData data, CancellationToken ct)
        {
            // One reading position per profile and target; formats share it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "Locator", "Progression", "IsCompleted", "CompletedAt")
                SELECT
                    @ProfileId,
                    asset."WorkId",
                    asset."WorkUnitId",
                    2,
                    @Locator,
                    @Progression,
                    @IsCompleted,
                    CASE WHEN @IsCompleted THEN now() END
                FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" IN (3, 4)
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                ON CONFLICT ("ProfileId", "WorkId", "WorkUnitId") DO UPDATE
                SET
                    "ProgressPositionTypeId" = 2,
                    "Locator" = EXCLUDED."Locator",
                    "Progression" = EXCLUDED."Progression",
                    "IsCompleted" = "MediaProgress"."IsCompleted" OR EXCLUDED."IsCompleted",
                    "CompletedAt" = coalesce("MediaProgress"."CompletedAt", EXCLUDED."CompletedAt"),
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The reading target was not found.");
            }
        }
    }
}
