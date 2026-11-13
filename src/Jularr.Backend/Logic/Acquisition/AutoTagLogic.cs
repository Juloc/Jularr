using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The tags Jularr keeps itself on every title with a target: its media type, original language, genres and the profiles that requested it.
    // Each run brings them in line with the titles and drops those no title carries and no release profile asks for; tags a person named stay untouched.
    internal static class AutoTagLogic
    {
        private const string Desired =
            """
            WITH targets AS (
                SELECT DISTINCT wanted."WorkId" FROM "WantedItems" AS wanted
            ), desired AS (
                SELECT target."WorkId", 'kind:' || lower(kind."Key") AS "AutoKey"
                FROM targets AS target
                    JOIN "WorkAcquisitionKinds" AS chosen ON chosen."WorkId" = target."WorkId"
                    JOIN "AcquisitionMediaKinds" AS kind ON kind."Id" = chosen."AcquisitionMediaKindId"
                UNION
                SELECT target."WorkId", 'language:' || work."OriginalLanguage"
                FROM targets AS target JOIN "Works" AS work ON work."Id" = target."WorkId"
                WHERE work."OriginalLanguage" IS NOT NULL
                UNION
                SELECT target."WorkId", 'genre:' || genre::text
                FROM targets AS target JOIN "Works" AS work ON work."Id" = target."WorkId" CROSS JOIN unnest(work."GenreIds") AS genre
                UNION
                SELECT target."WorkId", 'requester:' || request."RequestedByProfileId"::text
                FROM targets AS target JOIN "AcquisitionRequests" AS request ON request."WorkId" = target."WorkId"
                WHERE request."RequestedByProfileId" IS NOT NULL
            )
            """;

        public static async Task<int> RefreshAutoTagsAsync(this LogicContext ctx, CancellationToken ct)
        {
            // New keys become tags first; their label is the key, so it never meets a tag a person named.
            var sql = new SqlStatement();
            sql.Statement = Desired +
                """
                INSERT INTO "Tags" ("Label", "AutoKey")
                SELECT DISTINCT 'auto:' || desired."AutoKey", desired."AutoKey" FROM desired
                WHERE NOT EXISTS (SELECT 1 FROM "Tags" AS known WHERE known."AutoKey" = desired."AutoKey")
                ORDER BY 2
                ON CONFLICT DO NOTHING;
                """;
            var changed = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // A title loses the kept tags it no longer earns and gains the ones it does.
            sql = new SqlStatement();
            sql.Statement = Desired +
                """
                DELETE FROM "WorkTags" AS tagged
                USING "Tags" AS tag
                WHERE tag."Id" = tagged."TagId"
                    AND tag."AutoKey" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM desired WHERE desired."WorkId" = tagged."WorkId" AND desired."AutoKey" = tag."AutoKey");
                """;
            changed += await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement = Desired +
                """
                INSERT INTO "WorkTags" ("WorkId", "TagId")
                SELECT desired."WorkId", tag."Id" FROM desired JOIN "Tags" AS tag ON tag."AutoKey" = desired."AutoKey"
                ORDER BY 1, 2
                ON CONFLICT ("WorkId", "TagId") DO NOTHING;
                """;
            changed += await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // A kept tag no title carries and no release profile asks for leaves.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM "Tags" AS tag
                WHERE tag."AutoKey" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM "WorkTags" AS tagged WHERE tagged."TagId" = tag."Id")
                    AND NOT EXISTS (SELECT 1 FROM "ReleaseProfiles" AS profile WHERE tag."Id" = ANY(profile."TagIds"));
                """;
            changed += await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return changed;
        }
    }
}
