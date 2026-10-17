using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Artwork.V1;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class ArtworkCacheLogic
    {
        // Content-key references are ordered by oldest effective rendition, not by user activity.
        // All references to one file are removed together before its bytes can be pruned.
        public static Task<IReadOnlyList<ArtworkStoredRenditionData>> ReadArtworkStoredRenditionsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    "CacheKey",
                    "FormatId"
                FROM
                    "ArtworkRenditions"
                WHERE
                    "FormatId" IN (3, 4)
                GROUP BY
                    "CacheKey", "FormatId"
                ORDER BY
                    max("CreatedAt"), "CacheKey";
                """;
            return ctx.ReadSql.ReadListAsync<ArtworkStoredRenditionData>(sql, ct);
        }

        public static Task<int> RetireArtworkCacheFileAsync(this LogicContext ctx, ArtworkStoredRenditionData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ArtworkRenditions"
                WHERE
                    "CacheKey" = @CacheKey
                    AND "FormatId" = @FormatId;
                """;
            sql.Parameters.Add(data);
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<ArtworkCacheSettingsData> ReadArtworkCacheSettingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    "CacheQualityId",
                    "CacheLimitBytes",
                    "CacheFormatId",
                    "ProgressiveJxl"
                FROM
                    "ArtworkSettings"
                WHERE
                    "Id" = 1;
                """;
            return ctx.ReadSql.ReadRequiredAsync<ArtworkCacheSettingsData>(sql, ct);
        }

        public static Task<IReadOnlyList<ArtworkCacheCandidateData>> ReadArtworkCacheCandidatesAsync(this LogicContext ctx, ArtworkCacheRootData root, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    artwork."Id" AS "ArtworkId",
                    artwork."WorkId",
                    artwork."WorkUnitId",
                    artwork."SlotId",
                    artwork."SourceKey",
                    artwork."SourceSizeBytes"
                FROM
                    "WorkArtworks" AS artwork
                    CROSS JOIN "ArtworkSettings" AS settings
                WHERE
                    artwork."LibraryRootId" = @LibraryRootId
                    AND artwork."SourceKindId" = 2
                    AND artwork."SourceSizeBytes" BETWEEN 1 AND 12582912
                    AND settings."CacheLimitBytes" > 0
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "ArtworkRenditions" AS rendition
                        WHERE
                            rendition."WorkArtworkId" = artwork."Id"
                            AND rendition."FormatId" = 3
                            AND rendition."QualityId" = settings."CacheQualityId"
                            AND rendition."CreatedAt" >= artwork."UpdatedAt"
                    )
                    OR (
                        artwork."LibraryRootId" = @LibraryRootId
                        AND artwork."SourceKindId" = 2
                            AND artwork."SourceSizeBytes" BETWEEN 1 AND 12582912
                        AND settings."CacheLimitBytes" > 0
                        AND settings."CacheFormatId" = 2
                        AND NOT EXISTS (
                            SELECT 1 FROM "ArtworkRenditions" AS jxl
                            WHERE jxl."WorkArtworkId" = artwork."Id"
                                AND jxl."FormatId" = 4
                                AND jxl."QualityId" = settings."CacheQualityId"
                                AND jxl."IsProgressive" = settings."ProgressiveJxl"
                                AND jxl."CreatedAt" >= artwork."UpdatedAt"
                        )
                    )
                ORDER BY
                    artwork."UpdatedAt", artwork."Id"
                LIMIT 100;
                """;
            sql.Parameters.Add(root);
            return ctx.ReadSql.ReadListAsync<ArtworkCacheCandidateData>(sql, ct);
        }

        public static async Task SaveArtworkRenditionAsync(this LogicContext ctx, ArtworkRenditionSaveData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkArtworks"
                SET
                    "SourceHash" = @SourceHash
                WHERE
                    "Id" = @ArtworkId
                    AND "WorkId" = @WorkId;

                INSERT INTO
                    "ArtworkRenditions" ("WorkArtworkId", "FormatId", "Width", "Height", "CacheKey", "ContentHash", "SizeBytes", "QualityId", "IsProgressive", "EncoderVersion")
                VALUES
                    (@ArtworkId, @FormatId, @Width, @Height, @CacheKey, @ContentHash, @SizeBytes, @QualityId, @IsProgressive,
                        CASE WHEN @FormatId = 4 THEN 'libjxl-cjxl-v1' ELSE 'skia-webp-v1' END)
                ON CONFLICT ("WorkArtworkId", "FormatId", "Width", "Height") DO UPDATE
                SET
                    "CacheKey" = EXCLUDED."CacheKey",
                    "ContentHash" = EXCLUDED."ContentHash",
                    "SizeBytes" = EXCLUDED."SizeBytes",
                    "QualityId" = EXCLUDED."QualityId",
                    "IsProgressive" = EXCLUDED."IsProgressive",
                    "EncoderVersion" = EXCLUDED."EncoderVersion",
                    "CreatedAt" = now();

                INSERT INTO
                    "ArtworkFallbacks" ("WorkId", "WorkUnitId", "SlotId", "WebpBytes", "ContentHash")
                SELECT
                    @WorkId, @WorkUnitId, @SlotId, @FallbackBytes, @FallbackHash
                WHERE
                    @FallbackBytes::bytea IS NOT NULL
                ON CONFLICT ("WorkId", "WorkUnitId", "SlotId", "Locale") DO UPDATE
                SET
                    "WebpBytes" = EXCLUDED."WebpBytes",
                    "ContentHash" = EXCLUDED."ContentHash",
                    "UpdatedAt" = now()
                WHERE
                    "ArtworkFallbacks"."Locale" IS NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<ArtworkPreviewData?> ReadArtworkPreviewAsync(this LogicContext ctx, ArtworkReadParametersV1 query, ProfileParameters profile, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    rendition."CacheKey",
                    jxl."JxlCacheKey",
                    fallback."WebpBytes" AS "FallbackBytes"
                FROM
                    "ProfileWorks" AS visible
                    LEFT JOIN LATERAL (
                        SELECT
                            variant."CacheKey"
                        FROM
                            "WorkArtworks" AS image
                            JOIN "ArtworkRenditions" AS variant ON variant."WorkArtworkId" = image."Id" AND variant."FormatId" = 3
                        WHERE
                            image."WorkId" = visible."WorkId"
                            AND image."WorkUnitId" IS NOT DISTINCT FROM @WorkUnitId
                            AND image."SlotId" = @SlotId
                        ORDER BY
                            image."IsPinned" DESC,
                            image."SourceKindId",
                            variant."CreatedAt" DESC,
                            image."Id"
                        LIMIT 1
                    ) AS rendition ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            variant."CacheKey" AS "JxlCacheKey"
                        FROM
                            "WorkArtworks" AS image
                            JOIN "ArtworkRenditions" AS variant ON variant."WorkArtworkId" = image."Id" AND variant."FormatId" = 4
                        WHERE
                            image."WorkId" = visible."WorkId"
                            AND image."WorkUnitId" IS NOT DISTINCT FROM @WorkUnitId
                            AND image."SlotId" = @SlotId
                        ORDER BY
                            image."IsPinned" DESC,
                            image."SourceKindId",
                            variant."CreatedAt" DESC,
                            image."Id"
                        LIMIT 1
                    ) AS jxl ON TRUE
                    LEFT JOIN "ArtworkFallbacks" AS fallback ON fallback."WorkId" = visible."WorkId"
                        AND fallback."WorkUnitId" IS NOT DISTINCT FROM @WorkUnitId
                        AND fallback."Locale" IS NULL AND fallback."SlotId" = @SlotId
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND visible."WorkId" = @WorkId
                    AND NOT visible."IsHidden";
                """;
            sql.Parameters.Add(query);
            sql.Parameters.Add(profile);
            return ctx.ReadSql.ReadOptionalAsync<ArtworkPreviewData>(sql, ct);
        }
    }
}
