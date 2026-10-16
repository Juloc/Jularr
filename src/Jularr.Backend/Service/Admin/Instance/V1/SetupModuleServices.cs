using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/setup/modules")]
    public sealed class ReadSetupModulesV1 : AdminService<SetupProgressParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SetupModuleV1>>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Presets select the canonical switches, including local account requests with Owner approval.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    module."Id",
                    module."Key",
                    module."IsEnabled",
                    module."Version",
                    TRUE AS "Complete",
                    module."Key" NOT IN ('Requests', 'Acquisition', 'Uploads') AS "MediaServer",
                    module."Key" IN ('Requests', 'Acquisition', 'Notifications', 'Profiles', 'Devices', 'Diagnostics', 'Registration') AS "Requests"
                FROM
                    "InstanceModules" AS module
                ORDER BY
                    module."Id";
                """;
            return new BaseDataList<SetupModuleV1>(await Context.ReadSql.ReadListAsync<SetupModuleV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/setup/modules")]
    public sealed class SaveSetupModulesV1 : AdminService<SetupProgressParametersV1, SaveSetupModulesDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.RequireSetupVersionAsync(new SetupVersionData(Data.SetupVersion), CancellationToken);
            var settings = await Context.ReadInitialInstanceSettingsAsync(CancellationToken);
            await Context.UpdateInstanceSettingsAsync(new InstanceSettingsUpdateData(Data.Name, settings.UiLocaleId, Data.Version, null, null, null, null, null, null, null, null, null,
                settings.PublicUrl, null), CancellationToken);
            await Context.UpdateInstanceModulesAsync(Data.Modules.Select(module => new InstanceModuleUpdateData(module.Id, module.IsEnabled, module.Version)).ToArray(), CancellationToken);
            await Context.UpdateSetupStepAsync(new SetupStepData(SetupStepType.Storage), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
