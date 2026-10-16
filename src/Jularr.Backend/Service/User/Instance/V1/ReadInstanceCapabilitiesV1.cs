using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.User.Instance.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/instance/capabilities")]
    public sealed class ReadInstanceCapabilitiesV1 : UserService<ReadInstanceCapabilitiesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<InstanceCapabilitiesDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadInstanceCapabilitiesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadInstanceCapabilitiesParametersV1 parameters, Type resultType) => new("instance.capabilities.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read actual module availability.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    module."Key",
                    module."IsEnabled"
                FROM
                    "InstanceModules" AS module;
                """;
            var modules = await Context.ReadSql.ReadListAsync<InstanceModuleData>(sql, CancellationToken);

            var usable = modules.Where(module => module.IsEnabled).Select(module => module.Key);
            var permissions = ServiceGate.GetPermissions(Auth.AccountId, Auth.ProfileId, Auth.ProfileOwnerAccountId, Auth.AccountRoleTypeId, Auth.IsDevice);
            return new InstanceCapabilitiesDetailsV1([.. permissions], [.. usable]);
        }
    }
}
