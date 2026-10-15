using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Fields;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Library
{
    // What happened, named by its ids; the history reads the texts it keeps from them.
    internal sealed class ActivityEventData : BaseData
    {
        public ActivityEventData(ActivityEventType activityEventTypeId, long? workId = null, long? operationId = null, long? acquisitionReleaseId = null, long? storedFileId = null,
            long? accountId = null, string? previousPath = null, string? reasonKey = null)
        {
            ActivityEventTypeId = activityEventTypeId;
            WorkId = workId;
            OperationId = operationId;
            AcquisitionReleaseId = acquisitionReleaseId;
            StoredFileId = storedFileId;
            AccountId = accountId;
            PreviousPath = previousPath;
            ReasonKey = reasonKey;
        }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ActivityEventTypeId))]
        public ActivityEventType ActivityEventTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        // A download names its release, client and the account that asked for it.
        [DataField(typeof(OperationFields), nameof(OperationFields.Id), false)]
        public long? OperationId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Id), false)]
        public long? AcquisitionReleaseId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id), false)]
        public long? StoredFileId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id), false)]
        public long? AccountId { get; }

        // A renamed file's former place below its library.
        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.Path))]
        public string? PreviousPath { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ReasonKey))]
        public string? ReasonKey { get; }
    }

    internal sealed class ActivityWorkData : BaseData
    {
        public ActivityWorkData(long? workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }
    }
}
