using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Public.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/instance/presentation")]
    public sealed class ReadInstancePresentationV1 : PublicService<ReadInstancePresentationParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<InstancePresentationDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadInstancePresentationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnRead()
        {
            // Public display values only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."Name",
                    settings."UiLocaleId",
                    settings."PublicUrl",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "InstanceModules" AS module
                        WHERE
                            module."Key" = 'Devices'
                            AND module."IsEnabled"
                    ) AS "PairsDevices"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return await Context.ReadSql.ReadRequiredAsync<InstancePresentationDetailsV1>(sql, CancellationToken);
        }
    }
}
