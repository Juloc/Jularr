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
                    "InstanceModules" AS module
                WHERE
                    module."Key" IN ('Library', 'Requests');
                """;
            var modules = await Context.ReadSql.ReadListAsync<InstanceModuleData>(sql, CancellationToken);
            var library = modules.Any(module => module.Key == "Library" && module.IsEnabled);
            var requests = modules.Any(module => module.Key == "Requests" && module.IsEnabled);

            // Reuse verified central grants.
            var permissions = ServiceGate.GetPermissions(Auth.AccountId, Auth.ProfileId, Auth.ProfileOwnerAccountId, Auth.AccountRoleTypeId);
            return new InstanceCapabilitiesDetailsV1(
                library && permissions.Contains("profile.media.read", StringComparer.Ordinal),
                library && permissions.Contains("media.manual.update", StringComparer.Ordinal),
                library && permissions.Contains("profile.watchlist.update", StringComparer.Ordinal),
                requests && permissions.Contains("profile.requests.update", StringComparer.Ordinal),
                requests && permissions.Contains("requests.update", StringComparer.Ordinal),
                requests && permissions.Contains("wanted.update", StringComparer.Ordinal));
        }
    }
}
