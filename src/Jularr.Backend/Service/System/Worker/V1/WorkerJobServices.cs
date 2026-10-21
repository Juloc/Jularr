using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Worker;
using Jularr.Backend.Data.System.Worker.V1;
using Jularr.Backend.Logic.Worker;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Worker.V1
{
    // A worker reads the Owner's choices and intervals once, before its first pass, so a paused job does not run after a restart.
    public sealed class LoadWorkerJobsV1 : SystemService<LoadWorkerJobsParametersV1, NoData>
    {
        private readonly WorkerJobs _jobs;

        internal LoadWorkerJobsV1(WorkerJobs jobs)
        {
            _jobs = jobs;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LoadWorkerJobsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(LoadWorkerJobsParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (rows, settings) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await ctx.ReadWorkerJobsAsync(ct), await ctx.ReadWorkerSettingsAsync(ct)), CancellationToken);
            _jobs.Load(rows, settings);
            return new UpdateResult(true);
        }
    }

    // A job's finished pass is kept; a failure also becomes a notice in Activity until the job runs well again.
    public sealed class RecordWorkerJobV1 : SystemService<WorkerJobParametersV1, RecordWorkerJobDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkerJobParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(WorkerJobParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            _ = WorkerJobs.Definition(Parameters.Key);
            await Context.SaveWorkerJobRunAsync(new WorkerJobRunData(Parameters.Key, Data.StartedAt, Data.FinishedAt, Data.ResultTypeId, Data.ErrorKey, Data.NextRunAt), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
