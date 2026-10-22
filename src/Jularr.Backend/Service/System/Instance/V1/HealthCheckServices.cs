using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Instance.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Instance.V1
{
    // The health job's pass: checks every configured service and keeps the answer for the Dashboard. The job reports a failure when a service did not answer.
    public sealed class RunHealthCheckV1 : SystemService<HealthCheckParametersV1, NoData>
    {
        private readonly HealthProbes _probes;
        private readonly WorkerJobs _jobs;

        internal RunHealthCheckV1(HealthProbes probes, WorkerJobs jobs)
        {
            _probes = probes;
            _jobs = jobs;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<HealthV1>()];
        protected override ModuleRequirement GetInstanceModules(HealthCheckParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(HealthCheckParametersV1 parameters, Type resultType) => new("system.operations");

        // Asked for by the Owner it does not reuse the minute's answer.
        protected override async Task<IServiceOutput> OnExecute() => await _probes.RunAsync(Context, _jobs.IsRequestedRun("health"), CancellationToken);
    }
}
