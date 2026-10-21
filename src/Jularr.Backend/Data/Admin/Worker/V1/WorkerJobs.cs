using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.Fields.Worker;

namespace Jularr.Backend.Data.Admin.Worker.V1
{
    public sealed class ReadWorkerJobsParametersV1 : BaseParameters
    {
    }

    public sealed class JobParametersV1 : BaseParameters
    {
        public JobParametersV1(string key)
        {
            Key = key;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.Key))]
        public string Key { get; }
    }

    public sealed class PauseWorkerJobDataV1 : BaseData
    {
        public PauseWorkerJobDataV1(bool isPaused)
        {
            IsPaused = isPaused;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.IsPaused))]
        public bool IsPaused { get; }
    }

    public sealed class WorkerJobUseDataV1 : BaseData
    {
        public WorkerJobUseDataV1(bool pausesWhileUsed)
        {
            PausesWhileUsed = pausesWhileUsed;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }
    }

    // A background job with its worker, schedule, the Owner's choices and how it last ran.
    public sealed class WorkerJobV1 : BaseResponse
    {
        public WorkerJobV1(string key, WorkerGroupType workerGroupTypeId, WorkerJobStateType workerJobStateTypeId, int? intervalSeconds, bool isPaused, bool pausesWhileUsed, bool canPauseWhileUsed,
            bool canRunNow, DateTime? lastStartedAt, DateTime? lastFinishedAt, DateTime? nextRunAt, string? lastErrorKey, string? parentKey)
        {
            ParentKey = parentKey;
            Key = key;
            WorkerGroupTypeId = workerGroupTypeId;
            WorkerJobStateTypeId = workerJobStateTypeId;
            IntervalSeconds = intervalSeconds;
            IsPaused = isPaused;
            PausesWhileUsed = pausesWhileUsed;
            CanPauseWhileUsed = canPauseWhileUsed;
            CanRunNow = canRunNow;
            LastStartedAt = lastStartedAt;
            LastFinishedAt = lastFinishedAt;
            NextRunAt = nextRunAt;
            LastErrorKey = lastErrorKey;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.Key))]
        public string Key { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.WorkerGroupTypeId))]
        public WorkerGroupType WorkerGroupTypeId { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.WorkerJobStateTypeId))]
        public WorkerJobStateType WorkerJobStateTypeId { get; }

        // The job this one is a part of, such as the provider sync of one server; it shares that job's schedule.
        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.ParentKey))]
        public string? ParentKey { get; }

        // Between two passes, 0 when switched off; none for a job that runs when work arrives.
        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.IntervalSeconds))]
        public int? IntervalSeconds { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.CanPauseWhileUsed))]
        public bool CanPauseWhileUsed { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.CanRunNow))]
        public bool CanRunNow { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.LastStartedAt))]
        public DateTime? LastStartedAt { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.LastFinishedAt))]
        public DateTime? LastFinishedAt { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.NextRunAt))]
        public DateTime? NextRunAt { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.LastErrorKey))]
        public string? LastErrorKey { get; }
    }
}
