using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Instance
{
    internal static class SetupLogic
    {
        public static async Task RequireSetupVersionAsync(this LogicContext ctx, SetupVersionData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    setup."SetupStepTypeId",
                    setup."CompletedAt" IS NOT NULL AS "IsCompleted",
                    setup."Version"
                FROM
                    "InstanceSetupState" AS setup
                WHERE
                    setup."Id" = 1
                    AND setup."OwnerCreatedAt" IS NOT NULL
                FOR UPDATE;
                """;
            var state = await ctx.ReadRequiredAsync<SetupStateData>(sql, ct);
            if (state.IsCompleted || state.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");
        }

        public static Task<SetupStateData> ReadSetupStateAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    setup."SetupStepTypeId",
                    setup."CompletedAt" IS NOT NULL AS "IsCompleted",
                    setup."Version"
                FROM
                    "InstanceSetupState" AS setup
                WHERE
                    setup."Id" = 1;
                """;
            return ctx.ReadSql.ReadRequiredAsync<SetupStateData>(sql, ct);
        }

        // Blocking or a warning.
        public static Task<IReadOnlyList<SetupCheckData>> ReadSetupChecksAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    CASE WHEN category."MediaSubtypeId" IS NULL THEN 'library.type.' ELSE 'library.subtype.' END || category."Key" AS "CheckKey",
                    TRUE AS "IsBlocking"
                FROM
                    "MediaCategories" AS category
                WHERE
                    category."IsAvailable"
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "LibraryRoots" AS root
                        WHERE
                            root."MediaTypeId" = category."MediaTypeId"
                            OR root."MediaSubtypeId" = category."MediaSubtypeId"
                    )
                UNION ALL
                SELECT
                    'tmdb',
                    TRUE
                WHERE
                    EXISTS (SELECT 1 FROM "MediaTypes" AS type WHERE type."Key" IN ('Movie', 'Series') AND type."IsEnabled")
                    AND NOT EXISTS (SELECT 1 FROM "Providers" AS provider WHERE provider."Key" = 'tmdb' AND provider."IsEnabled" AND provider."ProtectedSecret" IS NOT NULL)
                UNION ALL
                SELECT
                    'indexer',
                    FALSE
                WHERE
                    EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" = 'Acquisition' AND module."IsEnabled")
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionConnections" AS connection
                            JOIN "AcquisitionConnectionTypes" AS type ON type."Id" = connection."AcquisitionConnectionTypeId"
                        WHERE
                            type."Key" = 'Newznab'
                            AND connection."IsEnabled"
                    )
                UNION ALL
                SELECT
                    'downloader',
                    FALSE
                WHERE
                    EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" = 'Acquisition' AND module."IsEnabled")
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionConnections" AS connection
                            JOIN "AcquisitionConnectionTypes" AS type ON type."Id" = connection."AcquisitionConnectionTypeId"
                        WHERE
                            type."Key" IN ('Sabnzbd', 'Usenet')
                            AND connection."IsEnabled"
                    );
                """;
            return ctx.ReadSql.ReadListAsync<SetupCheckData>(sql, ct);
        }

        public static async Task UpdateSetupStepAsync(this LogicContext ctx, SetupStepData data, CancellationToken ct)
        {
            // Only forward and only while the wizard runs.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSetupState" AS setup
                SET
                    "SetupStepTypeId" = greatest(setup."SetupStepTypeId", @SetupStepTypeId),
                    "Version" = setup."Version" + 1
                WHERE
                    setup."Id" = 1
                    AND setup."OwnerCreatedAt" IS NOT NULL
                    AND setup."CompletedAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.SetupStepTypeId), "invalid_transition");
            }
        }

        public static async Task CompleteSetupAsync(this LogicContext ctx, SetupVersionData data, CancellationToken ct)
        {
            // Lock, then recheck.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    setup."SetupStepTypeId",
                    setup."CompletedAt" IS NOT NULL AS "IsCompleted",
                    setup."Version"
                FROM
                    "InstanceSetupState" AS setup
                WHERE
                    setup."Id" = 1
                FOR UPDATE;
                """;
            var state = await ctx.ReadRequiredAsync<SetupStateData>(sql, ct);
            if (state.Version != data.Version || state.IsCompleted)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }

            if ((await ctx.ReadSetupChecksAsync(ct)).Any(check => check.IsBlocking))
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSetupState" AS setup
                SET
                    "CompletedAt" = now(),
                    "Version" = setup."Version" + 1
                WHERE
                    setup."Id" = 1;
                """;
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }
        }
    }
}
