using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Instance
{
    internal static class InstanceModuleLogic
    {
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
