using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Worker;
using Jularr.Backend.Logic.Worker;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Worker.V1
{
    // Every background job with its state: what the process knows now, and what the database kept of the rest.
    [ServiceEndpoint("GET", "/api/v1/admin/jobs")]
    public sealed class ReadWorkerJobsV1 : AdminService<ReadWorkerJobsParametersV1>
    {
        private readonly WorkerJobs _jobs;

        internal ReadWorkerJobsV1(WorkerJobs jobs)
        {
            _jobs = jobs;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkerJobV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorkerJobsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadWorkerJobsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var rows = (await Context.ReadWorkerJobsAsync(CancellationToken)).ToDictionary(row => row.Key);
            return new BaseDataList<WorkerJobV1>([.. WorkerJobs.Definitions.Select(definition =>
            {
                var status = _jobs.Describe(definition.Key, rows.GetValueOrDefault(definition.Key));
                return new WorkerJobV1(definition.Key, definition.Group, status.State, definition.Interval is { } interval ? (int)interval.TotalSeconds : null, status.IsPaused, status.PausesWhileUsed,
                    definition.CanPauseWhileUsed, definition.CanRunNow, status.LastStartedAt, status.LastFinishedAt, status.NextRunAt, status.LastErrorKey);
            })]);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/jobs/{key}/pause")]
    public sealed class PauseWorkerJobV1 : AdminService<JobParametersV1, PauseWorkerJobDataV1>
    {
        private readonly WorkerJobs _jobs;

        internal PauseWorkerJobV1(WorkerJobs jobs)
        {
            _jobs = jobs;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(JobParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(JobParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            _ = WorkerJobs.Definition(Parameters.Key);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveWorkerJobChoiceAsync(new WorkerJobChoiceData(Parameters.Key, Data.IsPaused, null), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _jobs.Pause(Parameters.Key, Data.IsPaused);
    }

    // The job's next pass starts at once, in the worker that owns it.
    [ServiceEndpoint("POST", "/api/v1/admin/jobs/{key}/run")]
    public sealed class RunWorkerJobV1 : AdminService<JobParametersV1, NoData>
    {
        private readonly WorkerJobs _jobs;

        internal RunWorkerJobV1(WorkerJobs jobs)
        {
            _jobs = jobs;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(JobParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(JobParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override Task<IServiceOutput> OnExecute()
        {
            _ = WorkerJobs.Definition(Parameters.Key);
            if (!_jobs.RequestRun(Parameters.Key))
            {
                throw new DataValidationException(nameof(JobParametersV1.Key), "invalid_transition");
            }

            return Task.FromResult<IServiceOutput>(new UpdateResult(true));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/jobs/{key}/use")]
    public sealed class SetWorkerJobUseV1 : AdminService<JobParametersV1, WorkerJobUseDataV1>
    {
        private readonly WorkerJobs _jobs;

        internal SetWorkerJobUseV1(WorkerJobs jobs)
        {
            _jobs = jobs;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(JobParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(JobParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only a heavy job can wait while Jularr is used.
            if (!WorkerJobs.Definition(Parameters.Key).CanPauseWhileUsed)
            {
                throw new DataValidationException(nameof(JobParametersV1.Key), "invalid_value");
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveWorkerJobChoiceAsync(new WorkerJobChoiceData(Parameters.Key, null, Data.PausesWhileUsed), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _jobs.SetPausesWhileUsed(Parameters.Key, Data.PausesWhileUsed);
    }
}
