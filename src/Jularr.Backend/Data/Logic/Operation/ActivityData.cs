using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Operation
{
    internal sealed class ActivityFilterData : BaseData
    {
        public ActivityFilterData(ActivityFilterType activityFilterTypeId, bool torrentOnly = false, bool downloadsOnly = false, bool failedOnly = false, DownloadClientKind? downloadClientKindId = null,
            long? acquisitionConnectionId = null, MediaType? mediaTypeId = null, DownloadPriorityType? downloadPriorityTypeId = null)
        {
            DownloadPriorityTypeId = downloadPriorityTypeId;
            ActivityFilterTypeId = activityFilterTypeId;
            TorrentOnly = torrentOnly;
            DownloadsOnly = downloadsOnly;
            FailedOnly = failedOnly;
            DownloadClientKindId = downloadClientKindId;
            AcquisitionConnectionId = acquisitionConnectionId;
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.ActivityFilterTypeId))]
        public ActivityFilterType ActivityFilterTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.TorrentOnly))]
        public bool TorrentOnly { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadsOnly))]
        public bool DownloadsOnly { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.FailedOnly))]
        public bool FailedOnly { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId), false)]
        public DownloadClientKind? DownloadClientKindId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadPriorityTypeId), false)]
        public DownloadPriorityType? DownloadPriorityTypeId { get; }
    }

    internal sealed class OperationActionData : BaseData
    {
        public OperationActionData(long operationId, long? accountId)
        {
            OperationId = operationId;
            AccountId = accountId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id), false)]
        public long? AccountId { get; }
    }
}
