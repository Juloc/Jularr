using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Artwork.V1;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class ArtworkCacheLogic
    {
        public static Task<ArtworkCacheSettingsData> ReadArtworkCacheSettingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    "CacheQualityId",
                    "CacheLimitBytes"
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
                    artwork."SlotId",
                    artwork."SourceKey",
                    artwork."SourceSizeBytes"
                FROM
                    "WorkArtworks" AS artwork
                    CROSS JOIN "ArtworkSettings" AS settings
                WHERE
                    artwork."LibraryRootId" = @LibraryRootId
                    AND artwork."SourceKindId" = 2
                    AND artwork."WorkUnitId" IS NULL
                    AND artwork."SourceSizeBytes" BETWEEN 1 AND 12582912
                    AND lower(artwork."SourceKey") !~ '\.jxl$'
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
                    "ArtworkRenditions" ("WorkArtworkId", "FormatId", "Width", "Height", "CacheKey", "ContentHash", "SizeBytes", "QualityId", "EncoderVersion")
                VALUES
                    (@ArtworkId, 3, @Width, @Height, @CacheKey, @ContentHash, @SizeBytes, @QualityId, 'skia-webp-v1')
                ON CONFLICT ("WorkArtworkId", "FormatId", "Width", "Height") DO UPDATE
                SET
                    "CacheKey" = EXCLUDED."CacheKey",
                    "ContentHash" = EXCLUDED."ContentHash",
                    "SizeBytes" = EXCLUDED."SizeBytes",
                    "QualityId" = EXCLUDED."QualityId",
                    "EncoderVersion" = EXCLUDED."EncoderVersion",
                    "CreatedAt" = now();

                INSERT INTO
                    "ArtworkFallbacks" ("WorkId", "SlotId", "WebpBytes", "ContentHash")
                SELECT
                    @WorkId, @SlotId, @FallbackBytes, @FallbackHash
                WHERE
                    @FallbackBytes::bytea IS NOT NULL
                ON CONFLICT ("WorkId", "WorkUnitId", "SlotId", "Locale") DO UPDATE
                SET
                    "WebpBytes" = EXCLUDED."WebpBytes",
                    "ContentHash" = EXCLUDED."ContentHash",
                    "UpdatedAt" = now()
                WHERE
                    "ArtworkFallbacks"."WorkUnitId" IS NULL
                    AND "ArtworkFallbacks"."Locale" IS NULL;
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
                            AND image."WorkUnitId" IS NULL
                            AND image."SlotId" = @SlotId
                        ORDER BY
                            image."IsPinned" DESC,
                            image."SourceKindId",
                            variant."CreatedAt" DESC,
                            image."Id"
                        LIMIT 1
                    ) AS rendition ON TRUE
                    LEFT JOIN "ArtworkFallbacks" AS fallback ON fallback."WorkId" = visible."WorkId"
                        AND fallback."WorkUnitId" IS NULL AND fallback."Locale" IS NULL AND fallback."SlotId" = @SlotId
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
