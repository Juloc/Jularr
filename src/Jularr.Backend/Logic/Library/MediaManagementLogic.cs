using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    internal static class MediaManagementLogic
    {
        public static Task<MediaManagementSettingsData> ReadMediaManagementSettingsAsync(this LogicContext ctx, MediaType type, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."MediaTypeId",
                    settings."UsesHardlinks",
                    settings."SkipsFreeSpaceCheck",
                    settings."MinimumFreeSpaceMegabytes",
                    settings."AnalysesMedia",
                    settings."Version"
                FROM "MediaManagementSettings" AS settings
                WHERE settings."MediaTypeId" = @MediaTypeId;
                """;
            sql.Parameters.Add(new MediaManagementParameters(type));
            return ctx.ReadRequiredAsync<MediaManagementSettingsData>(sql, ct);
        }

        public static async Task SaveMediaManagementSettingsAsync(this LogicContext ctx, MediaManagementSettingsData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "MediaManagementSettings" AS settings
                SET
                    "UsesHardlinks" = @UsesHardlinks,
                    "SkipsFreeSpaceCheck" = @SkipsFreeSpaceCheck,
                    "MinimumFreeSpaceMegabytes" = @MinimumFreeSpaceMegabytes,
                    "AnalysesMedia" = @AnalysesMedia,
                    "Version" = settings."Version" + 1
                WHERE settings."MediaTypeId" = @MediaTypeId
                    AND settings."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
        }
    }
}
