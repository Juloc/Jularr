using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class DownloadCleanupIdsData(long[] ids) : BaseData
    {
        [DataField(typeof(OperationFields), nameof(OperationFields.Ids))]
        public long[] OperationIds { get; } = ids;
    }

    internal sealed class DownloadCleanupStateData(long operationId, bool isFinished, bool isMissing) : BaseData
    {
        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; } = operationId;

        [DataField(typeof(OperationFields), nameof(OperationFields.IsFinished))]
        public bool IsFinished { get; } = isFinished;

        [DataField(typeof(OperationFields), nameof(OperationFields.IsMissing))]
        public bool IsMissing { get; } = isMissing;
    }
}
