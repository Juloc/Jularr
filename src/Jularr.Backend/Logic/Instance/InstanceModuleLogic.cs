using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Instance
{
    internal static class InstanceModuleLogic
    {
        public static async Task UpdateInstanceModulesAsync(this LogicContext ctx, IReadOnlyList<InstanceModuleUpdateData> modules, CancellationToken ct)
        {
            // Match every shown row by ID and version; one stale or missing row rolls the step back.
            if (modules.Count is < 1 or > 64 || modules.Select(module => module.Id).Distinct().Count() != modules.Count)
            {
                throw new DataValidationException("Modules", "out_of_range");
            }

            var data = new InstanceModulesUpdateData([.. modules.Select(module => module.Id)], [.. modules.Select(module => module.Version)],
                [.. modules.Where(module => module.IsEnabled).Select(module => module.Id)]);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceModules" AS module
                SET
                    "IsEnabled" = module."Id" = ANY(@EnabledIds),
                    "Version" = module."Version" + 1
                FROM
                    unnest(@Ids, @Versions) AS input("Id", "Version")
                WHERE
                    module."Id" = input."Id"
                    AND module."Version" = input."Version"
                    AND cardinality(@Ids) = (SELECT count(*) FROM "InstanceModules");
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != modules.Count)
            {
                throw new DataValidationException("Modules", "outdated_version");
            }
        }

        public static async Task<IReadOnlyList<InstanceModuleData>> ReadInstanceModulesAsync(this LogicContext ctx, InstanceModuleParameters parameters, CancellationToken ct)
        {
            // Hold configuration through mutations.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    module."Key",
                    module."IsEnabled"
                FROM
                    "InstanceModules" AS module
                WHERE
                    module."Key" = ANY(@ModuleKeys)
                ORDER BY
                    module."Id"
                FOR SHARE OF
                    module;
                """;
            sql.Parameters.Add(parameters);
            return await ctx.Sql.ReadListAsync<InstanceModuleData>(sql, ct);
        }

        // The switches as they are, without locks; also for callers without a session.
        public static Task<IReadOnlyList<InstanceModuleData>> ReadModuleStatesAsync(this LogicContext ctx, InstanceModuleParameters parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    module."Key",
                    module."IsEnabled"
                FROM
                    "InstanceModules" AS module
                WHERE
                    module."Key" = ANY(@ModuleKeys);
                """;
            sql.Parameters.Add(parameters);
            return ctx.ReadSql.ReadListAsync<InstanceModuleData>(sql, ct);
        }

        public static async Task UpdateInstanceModuleAsync(this LogicContext ctx, InstanceModuleUpdateData data, CancellationToken ct)
        {
            // Retain data when disabling.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceModules" AS module
                SET
                    "IsEnabled" = @IsEnabled,
                    "Version" = module."Version" + 1
                WHERE
                    module."Id" = @Id
                    AND module."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }
    }
}
