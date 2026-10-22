using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.System.V1
{
    // The last health check: every configured service and whether all of them answer. Nothing here asks a service; before the first check it shows what is stored.
    [ServiceEndpoint("GET", "/api/v1/admin/system/health")]
    public sealed class ReadHealthV1 : AdminService<ReadHealthParametersV1>
    {
        private readonly HealthChecks _checks;
        private readonly HealthProbes _probes;

        internal ReadHealthV1(HealthChecks checks, HealthProbes probes)
        {
            _checks = checks;
            _probes = probes;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<HealthV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadHealthParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadHealthParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead() => _checks.Current ?? await _probes.ReadStoredAsync(Context, CancellationToken);
    }

    // The Owner asks for a new check at once; it ignores the minute's answer.
    [ServiceEndpoint("POST", "/api/v1/admin/system/health/check")]
    public sealed class CheckHealthNowV1 : AdminService<CheckHealthParametersV1, NoData>
    {
        private readonly HealthProbes _probes;

        internal CheckHealthNowV1(HealthProbes probes)
        {
            _probes = probes;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<HealthV1>()];
        protected override ModuleRequirement GetInstanceModules(CheckHealthParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CheckHealthParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute() => await _probes.RunAsync(Context, true, CancellationToken);
    }
}
