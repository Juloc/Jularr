using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.System.Backup.V1
{
    public sealed class BackupScheduleParametersV1 : BaseParameters
    {
        public BackupScheduleParametersV1(bool isForced = false)
        {
            IsForced = isForced;
        }

        // An Owner's run now queues a backup whatever the interval says.
        [DataField(typeof(BackupFields), nameof(BackupFields.IsForced))]
        public bool IsForced { get; }
    }
}
