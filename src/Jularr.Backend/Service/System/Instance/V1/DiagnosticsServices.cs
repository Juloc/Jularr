using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Instance.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Instance.V1
{
    // Whether diagnostics are collected in memory.
    public sealed class ReadDiagnosticsStateV1 : SystemService<DiagnosticsStateParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Read;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DiagnosticsStateV1>()];
        protected override ModuleRequirement GetInstanceModules(DiagnosticsStateParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(DiagnosticsStateParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    module."IsEnabled"
                FROM
                    "InstanceModules" AS module
                WHERE
                    module."Key" = 'Diagnostics';
                """;
            return await Context.ReadSql.ReadRequiredAsync<DiagnosticsStateV1>(sql, CancellationToken);
        }
    }
}
