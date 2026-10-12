using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/instance/modules")]
    public sealed class ReadInstanceModulesV1 : AdminService<ReadInstanceModulesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<InstanceModuleDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadInstanceModulesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadInstanceModulesParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read implemented module configuration.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    module."Id",
                    module."Key",
                    module."IsEnabled",
                    module."Version"
                FROM
                    "InstanceModules" AS module
                ORDER BY
                    module."Id";
                """;
            return new BaseDataList<InstanceModuleDetailsV1>(await Context.ReadSql.ReadListAsync<InstanceModuleDetailsV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/instance/modules/{instanceModuleId:long}")]
    public sealed class UpdateInstanceModuleV1 : AdminService<UpdateInstanceModuleParametersV1, UpdateInstanceModuleDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateInstanceModuleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateInstanceModuleParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateInstanceModuleAsync(new InstanceModuleUpdateData(Parameters.InstanceModuleId, Data.IsEnabled, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
