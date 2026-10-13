using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class MediaCategoryLogic
    {
        public static async Task UpdateMediaCategoryAsync(this LogicContext ctx, MediaCategoryUpdateData data, CancellationToken ct)
        {
            // A type switches alone; a subtype only turns on below an enabled parent.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "MediaTypes" AS type
                SET
                    "IsEnabled" = @IsEnabled,
                    "Version" = type."Version" + 1
                WHERE
                    type."Key" = @Key
                    AND type."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1)
            {
                return;
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "MediaSubtypes" AS subtype
                SET
                    "IsEnabled" = @IsEnabled,
                    "Version" = subtype."Version" + 1
                WHERE
                    subtype."Key" = @Key
                    AND subtype."Version" = @Version
                    AND (NOT @IsEnabled OR EXISTS (
                        SELECT
                            1
                        FROM
                            "MediaSubtypeParents" AS link
                            JOIN "MediaTypes" AS type ON type."Id" = link."MediaTypeId"
                        WHERE
                            link."MediaSubtypeId" = subtype."Id"
                            AND type."IsEnabled"
                    ));
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }

        public static async Task SaveProfileMediaRulesAsync(this LogicContext ctx, ProfileMediaRulesData data, CancellationToken ct)
        {
            // Serialize edits per profile.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                FOR NO KEY UPDATE;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadOptionalAsync<ProfileParameters>(sql, ct) ?? throw new KeyNotFoundException("The profile was not found.");

            // Replace the rules the editor shows: blocks of available categories, hiding of usable ones. Then drop rules that say nothing.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ProfileMediaRules" AS rule
                SET
                    "IsBlocked" = rule."IsBlocked" AND @BlockedKeys::text[] IS NULL,
                    "IsHidden" = rule."IsHidden" AND (@HiddenKeys::text[] IS NULL OR rule."IsBlocked")
                FROM
                    "MediaCategories" AS category
                WHERE
                    rule."ProfileId" = @ProfileId
                    AND category."MediaTypeId" IS NOT DISTINCT FROM rule."MediaTypeId"
                    AND category."MediaSubtypeId" IS NOT DISTINCT FROM rule."MediaSubtypeId"
                    AND category."IsAvailable";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ProfileMediaRules" ("ProfileId", "MediaTypeId", "MediaSubtypeId", "IsBlocked", "IsHidden")
                SELECT
                    @ProfileId,
                    category."MediaTypeId",
                    category."MediaSubtypeId",
                    category."Key" = ANY(COALESCE(@BlockedKeys::text[], '{}')),
                    category."Key" = ANY(COALESCE(@HiddenKeys::text[], '{}'))
                FROM
                    "MediaCategories" AS category
                WHERE
                    category."Key" = ANY(COALESCE(@BlockedKeys::text[], '{}') || COALESCE(@HiddenKeys::text[], '{}'))
                ON CONFLICT ("ProfileId", "MediaTypeId", "MediaSubtypeId") DO UPDATE
                SET
                    "IsBlocked" = "ProfileMediaRules"."IsBlocked" OR excluded."IsBlocked",
                    "IsHidden" = "ProfileMediaRules"."IsHidden" OR excluded."IsHidden";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ProfileMediaRules" AS rule
                WHERE
                    rule."ProfileId" = @ProfileId
                    AND NOT rule."IsBlocked"
                    AND NOT rule."IsHidden";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
