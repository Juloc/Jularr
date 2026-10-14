using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Profile
{
    internal static class HomeLayoutLogic
    {
        public static async Task SaveHomeLayoutAsync(this LogicContext ctx, HomeLayoutSaveData data, CancellationToken ct)
        {
            // Every shelf exactly once; hidden ones are among them.
            var keys = Enum.GetNames<HomeShelfType>();
            if (data.ShelfKeys.Length != keys.Length || data.ShelfKeys.Distinct(StringComparer.Ordinal).Count() != keys.Length || data.ShelfKeys.Any(key => !keys.Contains(key, StringComparer.Ordinal)))
            {
                throw new DataValidationException(nameof(data.ShelfKeys), "unknown_or_duplicate_id");
            }

            if (data.HiddenKeys.Any(key => !keys.Contains(key, StringComparer.Ordinal)))
            {
                throw new DataValidationException(nameof(data.HiddenKeys), "unknown_id");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ProfileHomeShelves" AS shelf
                WHERE
                    shelf."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ProfileHomeShelves" ("ProfileId", "HomeShelfTypeId", "Position", "IsHidden")
                SELECT
                    @ProfileId,
                    type."Id",
                    chosen."Position",
                    type."Key" = ANY(@HiddenKeys)
                FROM
                    unnest(@ShelfKeys) WITH ORDINALITY AS chosen ("Key", "Position")
                    JOIN "HomeShelfTypes" AS type ON type."Key" = chosen."Key";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != keys.Length)
            {
                throw new DataValidationException(nameof(data.ShelfKeys), "unknown_id");
            }
        }
    }
}
