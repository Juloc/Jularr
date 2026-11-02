using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The release groups of each media type: collected from search results, typed by hand and rated for every profile of the type.
    internal static class ReleaseGroupLogic
    {
        // The most groups one search records; a search sees far fewer.
        public const int SightingLimit = 200;

        // The groups a release names: "danke-Empire, Ushi" (also with &, + or /) is two groups; each trimmed, once in any case, within the stored length.
        public static string[] Split(string? group) =>
            [.. (group ?? "").Split([',', '&', '+', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(name => name.Length <= 64).Distinct(StringComparer.OrdinalIgnoreCase)];

        // The group names of a search, once each in any case.
        public static string[] Sightings(IEnumerable<string?> groups) => [.. groups.SelectMany(Split).Distinct(StringComparer.OrdinalIgnoreCase).Take(SightingLimit)];

        public static async Task RecordReleaseGroupsAsync(this LogicContext ctx, ReleaseGroupSightingsData data, CancellationToken ct)
        {
            if (data.Names.Length == 0) return;

            // One statement for every group of the search: a new one is stored as first seen, a known one counts once more.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReleaseGroups" ("MediaTypeId", "Name", "SeenCount", "LastSeenAt")
                SELECT
                    @MediaTypeId,
                    seen."Name",
                    1,
                    @SeenAt
                FROM
                    (
                        SELECT DISTINCT ON (lower(name))
                            name AS "Name"
                        FROM
                            unnest(@Names::varchar(64)[]) AS name
                        ORDER BY
                            lower(name),
                            name
                    ) AS seen
                ON CONFLICT ("MediaTypeId", lower("Name")) DO UPDATE
                SET
                    "SeenCount" = least("ReleaseGroups"."SeenCount", 2147483646) + 1,
                    "LastSeenAt" = greatest("ReleaseGroups"."LastSeenAt", excluded."LastSeenAt");
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A group typed by hand; an existing one of any case is kept as it is.
        public static async Task<ReleaseGroupKeyData> AddReleaseGroupAsync(this LogicContext ctx, ReleaseGroupAddData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReleaseGroups" ("MediaTypeId", "Name")
                VALUES
                    (@MediaTypeId, @Name)
                ON CONFLICT ("MediaTypeId", lower("Name")) DO UPDATE
                SET
                    "Name" = "ReleaseGroups"."Name"
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return await ctx.ReadRequiredAsync<ReleaseGroupKeyData>(sql, ct);
        }

        // The rating applies to every profile of the group's media type at the next search.
        public static async Task RateReleaseGroupAsync(this LogicContext ctx, ReleaseGroupRatingData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReleaseGroups" AS item
                SET
                    "ReleaseGroupRatingId" = @ReleaseGroupRatingId
                WHERE
                    item."Id" = @Id;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new KeyNotFoundException("The release group was not found.");
        }
    }
}
