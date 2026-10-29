using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
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
                    setup."SetupStartTypeId",
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
                    setup."SetupStartTypeId",
                    setup."CompletedAt" IS NOT NULL AS "IsCompleted",
                    setup."Version"
                FROM
                    "InstanceSetupState" AS setup
                WHERE
                    setup."Id" = 1;
                """;
            return ctx.ReadSql.ReadRequiredAsync<SetupStateData>(sql, ct);
        }

        // What the chosen modules need and the instance still lacks. A required check ("blocking") is something the instance cannot do without in its chosen shape;
        // the Owner may still finish with it after confirming. The rest are warnings. A check is raised only while a module asks for what it checks.
        public static async Task<IReadOnlyList<SetupCheckData>> ReadSetupChecksAsync(this LogicContext ctx, CancellationToken ct)
        {
            var checks = new List<SetupCheckData>(await ctx.ReadStoredSetupChecksAsync(ct));

            // What the health check knows about stored connections: a storage that does not answer, an indexer or download client whose last test failed.
            var targets = await ctx.ReadHealthTargetsAsync(ct);
            if (targets.Mounts.Any(mount => HealthCheckLogic.Mount(mount).HealthStateTypeId == HealthStateType.Unavailable))
            {
                checks.Add(new SetupCheckData("storage.unavailable", false));
            }

            var failing = targets.Connections.Where(connection => HealthCheckLogic.StoredConnection(connection).HealthStateTypeId == HealthStateType.Unavailable).ToList();
            if (failing.Any(connection => HealthCheckLogic.ServiceType(connection) == HealthServiceType.Indexer))
            {
                checks.Add(new SetupCheckData("indexer.failing", false));
            }

            if (failing.Any(connection => HealthCheckLogic.ServiceType(connection) == HealthServiceType.DownloadClient))
            {
                checks.Add(new SetupCheckData("downloader.failing", false));
            }

            return checks;
        }

        private static Task<IReadOnlyList<SetupCheckData>> ReadStoredSetupChecksAsync(this LogicContext ctx, CancellationToken ct)
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
                    EXISTS (SELECT 1 FROM "MediaTypes" AS type WHERE type."Key" = 'Movie' AND type."IsEnabled")
                    AND NOT EXISTS (SELECT 1 FROM "Providers" AS provider WHERE provider."Key" = 'tmdb' AND provider."IsEnabled" AND provider."ProtectedSecret" IS NOT NULL)
                UNION ALL
                SELECT
                    'tvdb',
                    TRUE
                WHERE
                    EXISTS (SELECT 1 FROM "MediaTypes" AS type WHERE type."Key" = 'Series' AND type."IsEnabled")
                    AND NOT EXISTS (SELECT 1 FROM "Providers" AS provider WHERE provider."Key" = 'tvdb' AND provider."IsEnabled" AND provider."ProtectedSecret" IS NOT NULL)
                UNION ALL
                SELECT
                    'indexer',
                    TRUE
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
                    TRUE
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
                    )
                UNION ALL
                SELECT
                    'downloadStorage.' || CASE WHEN settings."DownloadStorageAreaId" = 1 THEN 'Usenet' ELSE 'Direct' END,
                    TRUE
                FROM
                    "DownloadStorageSettings" AS settings
                WHERE
                    settings."IncompleteStorageMountId" IS NULL
                    AND EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" = 'Acquisition' AND module."IsEnabled")
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionConnections" AS connection
                        WHERE
                            connection."IsEnabled"
                            AND CASE WHEN settings."DownloadStorageAreaId" = 1 THEN connection."AcquisitionConnectionTypeId" = 6
                                ELSE connection."AcquisitionConnectionTypeId" IN (3, 4, 5) END
                    )
                UNION ALL
                -- No basic function chosen: nothing of Jularr is switched on.
                SELECT
                    'modules',
                    TRUE
                WHERE
                    NOT EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" IN ('Library', 'Playback', 'Reading', 'Requests', 'Acquisition') AND module."IsEnabled")
                UNION ALL
                -- Libraries and downloads live on a storage connection.
                SELECT
                    'storage',
                    TRUE
                WHERE
                    EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" IN ('Library', 'Acquisition') AND module."IsEnabled")
                    AND NOT EXISTS (SELECT 1 FROM "StorageMounts")
                UNION ALL
                -- Starting as a takeover, nothing came over: no title, no indexer or client, no folder.
                SELECT
                    'takeover',
                    TRUE
                FROM
                    "InstanceSetupState" AS setup
                WHERE
                    setup."SetupStartTypeId" = 2
                    AND NOT EXISTS (SELECT 1 FROM "Works")
                    AND NOT EXISTS (SELECT 1 FROM "AcquisitionConnections")
                    AND NOT EXISTS (SELECT 1 FROM "LibraryRoots")
                UNION ALL
                -- The Library module without any library has nothing to show (media without a folder were switched off on leaving the step).
                SELECT
                    'library.none',
                    FALSE
                WHERE
                    EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" = 'Library' AND module."IsEnabled")
                    AND NOT EXISTS (SELECT 1 FROM "LibraryRoots")
                    AND NOT EXISTS (SELECT 1 FROM "MediaCategories" AS category WHERE category."IsAvailable")
                UNION ALL
                -- The default request rule was never saved, so requests run on the preset (manual approval, a limit per period).
                SELECT
                    'requests.default',
                    FALSE
                WHERE
                    EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" = 'Requests' AND module."IsEnabled")
                    AND EXISTS (SELECT 1 FROM "RequestRules" AS rule WHERE rule."IsDefault" AND rule."Version" = 1)
                UNION ALL
                -- Games are found through IGDB, which needs the Twitch application's pair.
                SELECT
                    'igdb',
                    TRUE
                WHERE
                    EXISTS (SELECT 1 FROM "MediaTypes" AS type WHERE type."Key" = 'Game' AND type."IsEnabled")
                    AND NOT EXISTS (SELECT 1 FROM "Providers" AS provider WHERE provider."Key" = 'igdb' AND provider."IsEnabled" AND provider."ProtectedSecret" IS NOT NULL)
                UNION ALL
                -- Profiles sign in to AniList with the instance's application; the catalog itself needs none.
                SELECT
                    'anilist',
                    FALSE
                WHERE
                    (SELECT count(*) FROM "InstanceModules" AS module WHERE module."Key" IN ('AniList', 'ProgressSync') AND module."IsEnabled") = 2
                    AND NOT EXISTS (SELECT 1 FROM "Providers" AS provider WHERE provider."Key" = 'anilist' AND provider."ProtectedSecret" IS NOT NULL);
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

        public static async Task UpdateSetupStartAsync(this LogicContext ctx, SetupStartData data, CancellationToken ct)
        {
            // The start is chosen while the wizard runs.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSetupState" AS setup
                SET
                    "SetupStartTypeId" = @SetupStartTypeId,
                    "Version" = setup."Version" + 1
                WHERE
                    setup."Id" = 1
                    AND setup."OwnerCreatedAt" IS NOT NULL
                    AND setup."CompletedAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.SetupStartTypeId), "invalid_transition");
            }
        }

        public static async Task CompleteSetupAsync(this LogicContext ctx, SetupFinishData data, CancellationToken ct)
        {
            // Lock, then recheck.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    setup."SetupStepTypeId",
                    setup."SetupStartTypeId",
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

            // Something required is missing: the Owner finishes anyway only after saying so.
            if (!data.AcceptMissing && (await ctx.ReadSetupChecksAsync(ct)).Any(check => check.IsBlocking))
            {
                throw new DataValidationException(nameof(data.AcceptMissing), "invalid_transition");
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
