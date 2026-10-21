using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.Fields.Worker;

namespace Jularr.Backend.Data.System.Worker.V1
{
    public sealed class LoadWorkerJobsParametersV1 : BaseParameters
    {
    }

    public sealed class WorkerJobParametersV1 : BaseParameters
    {
        public WorkerJobParametersV1(string key)
        {
            Key = key;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.Key))]
        public string Key { get; }
    }

    // One finished pass of a job as the worker reports it.
    public sealed class RecordWorkerJobDataV1 : BaseData
    {
        public RecordWorkerJobDataV1(DateTime? startedAt, DateTime finishedAt, WorkerJobResultType resultTypeId, string? errorKey, DateTime? nextRunAt)
        {
            StartedAt = startedAt;
            FinishedAt = finishedAt;
            ResultTypeId = resultTypeId;
            ErrorKey = errorKey;
            NextRunAt = nextRunAt;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.StartedAt))]
        public DateTime? StartedAt { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.FinishedAt))]
        public DateTime FinishedAt { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.ResultTypeId))]
        public WorkerJobResultType ResultTypeId { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.ErrorKey))]
        public string? ErrorKey { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.NextRunAt))]
        public DateTime? NextRunAt { get; }
    }
}
