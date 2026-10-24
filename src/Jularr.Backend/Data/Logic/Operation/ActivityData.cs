using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Operation
{
    internal sealed class ActivityFilterData : BaseData
    {
        public ActivityFilterData(ActivityFilterType activityFilterTypeId, bool torrentOnly = false)
        {
            ActivityFilterTypeId = activityFilterTypeId;
            TorrentOnly = torrentOnly;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.ActivityFilterTypeId))]
        public ActivityFilterType ActivityFilterTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.TorrentOnly))]
        public bool TorrentOnly { get; }
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
