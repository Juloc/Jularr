using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.Fields.Instance;
using Jularr.Backend.Data.Fields.Worker;

namespace Jularr.Backend.Data.Logic.Worker
{
    // What is kept of a job: the Owner's choices and how its last pass went.
    internal sealed class WorkerJobRowData : BaseData
    {
        public WorkerJobRowData(string key, bool isPaused, bool? pausesWhileUsedChoice, DateTime? lastStartedAt, DateTime? lastFinishedAt, WorkerJobResultType? lastResultTypeId, string? lastErrorKey,
            DateTime? nextRunAt)
        {
            Key = key;
            IsPaused = isPaused;
            PausesWhileUsedChoice = pausesWhileUsedChoice;
            LastStartedAt = lastStartedAt;
            LastFinishedAt = lastFinishedAt;
            LastResultTypeId = lastResultTypeId;
            LastErrorKey = lastErrorKey;
            NextRunAt = nextRunAt;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.Key))]
        public string Key { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.PausesWhileUsedChoice))]
        public bool? PausesWhileUsedChoice { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.LastStartedAt))]
        public DateTime? LastStartedAt { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.LastFinishedAt))]
        public DateTime? LastFinishedAt { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.LastResultTypeId))]
        public WorkerJobResultType? LastResultTypeId { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.LastErrorKey))]
        public string? LastErrorKey { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.NextRunAt))]
        public DateTime? NextRunAt { get; }
    }

    // One finished pass of a job.
    internal sealed class WorkerJobRunData : BaseData
    {
        public WorkerJobRunData(string key, DateTime? startedAt, DateTime finishedAt, WorkerJobResultType resultTypeId, string? errorKey, DateTime? nextRunAt)
        {
            Key = key;
            StartedAt = startedAt;
            FinishedAt = finishedAt;
            ResultTypeId = resultTypeId;
            ErrorKey = errorKey;
            NextRunAt = nextRunAt;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.Key))]
        public string Key { get; }

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

    // The Owner's choice for a job: pausing it, or whether it waits while Jularr is used.
    internal sealed class WorkerJobChoiceData : BaseData
    {
        public WorkerJobChoiceData(string key, bool? isPaused, bool? pausesWhileUsedChoice)
        {
            Key = key;
            IsPaused = isPaused;
            PausesWhileUsedChoice = pausesWhileUsedChoice;
        }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.Key))]
        public string Key { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.IsPaused), false)]
        public bool? IsPaused { get; }

        [DataField(typeof(WorkerJobFields), nameof(WorkerJobFields.PausesWhileUsedChoice))]
        public bool? PausesWhileUsedChoice { get; }
    }

    // The Owner's intervals for the jobs that have one.
    internal sealed class WorkerSettingsData : BaseData
    {
        public WorkerSettingsData(int backupIntervalHours, int backupKeep, int providerSyncIntervalMinutes, int wantedSearchIntervalMinutes)
        {
            BackupKeep = backupKeep;
            BackupIntervalHours = backupIntervalHours;
            ProviderSyncIntervalMinutes = providerSyncIntervalMinutes;
            WantedSearchIntervalMinutes = wantedSearchIntervalMinutes;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.BackupIntervalHours))]
        public int BackupIntervalHours { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.BackupKeep))]
        public int BackupKeep { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.ProviderSyncIntervalMinutes))]
        public int ProviderSyncIntervalMinutes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.WantedSearchIntervalMinutes))]
        public int WantedSearchIntervalMinutes { get; }
    }
}
