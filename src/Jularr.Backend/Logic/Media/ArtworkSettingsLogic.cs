using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class ArtworkSettingsLogic
    {
        public static Task<ArtworkSettingsDetailsV1> ReadAdminArtworkSettingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    "NasModeId",
                    "CacheFormatId",
                    "CacheQualityId",
                    "ProgressiveJxl",
                    "ExportToNas",
                    "CacheLimitBytes",
                    "Version"
                FROM
                    "ArtworkSettings"
                WHERE
                    "Id" = 1;
                """;
            return ctx.ReadSql.ReadRequiredAsync<ArtworkSettingsDetailsV1>(sql, ct);
        }

        public static async Task SaveAdminArtworkSettingsAsync(this LogicContext ctx, UpdateArtworkSettingsDataV1 data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ArtworkSettings"
                SET
                    "NasModeId" = @NasModeId,
                    "CacheFormatId" = @CacheFormatId,
                    "CacheQualityId" = @CacheQualityId,
                    "ProgressiveJxl" = @ProgressiveJxl,
                    "ExportToNas" = @ExportToNas,
                    "CacheLimitBytes" = @CacheLimitBytes,
                    "Version" = "Version" + 1
                WHERE
                    "Id" = 1
                    AND "Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }
    }
}
