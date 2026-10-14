using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Operation
{
    internal sealed class BackupAttemptData : BaseData
    {
        public BackupAttemptData(bool hasRecentAttempt)
        {
            HasRecentAttempt = hasRecentAttempt;
        }

        [DataField(typeof(BackupFields), nameof(BackupFields.HasRecentAttempt))]
        public bool HasRecentAttempt { get; }
    }

    internal sealed class BackupStateData : BaseData
    {
        public BackupStateData(bool isRunning, string? errorKey, bool canRestore)
        {
            IsRunning = isRunning;
            ErrorKey = errorKey;
            CanRestore = canRestore;
        }

        [DataField(typeof(BackupFields), nameof(BackupFields.IsRunning))]
        public bool IsRunning { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey))]
        public string? ErrorKey { get; }

        [DataField(typeof(BackupFields), nameof(BackupFields.CanRestore))]
        public bool CanRestore { get; }
    }
}
