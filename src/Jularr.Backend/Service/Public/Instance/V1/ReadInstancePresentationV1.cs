using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;
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
            // Public display values only; the switches come through the same read as every module check.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."Name",
                    settings."UiLocaleId",
                    settings."PublicUrl"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            var settings = await Context.ReadSql.ReadRequiredAsync<InstancePresentationSettingsData>(sql, CancellationToken);
            var modules = await Context.ReadModuleStatesAsync(new InstanceModuleParameters(["Devices", "Registration"]), CancellationToken);
            bool IsEnabled(string key) => modules.Any(module => module.Key == key && module.IsEnabled);
            return new InstancePresentationDetailsV1(settings.Name, settings.UiLocaleId, settings.PublicUrl, IsEnabled("Devices"), IsEnabled("Registration"));
        }
    }
}
