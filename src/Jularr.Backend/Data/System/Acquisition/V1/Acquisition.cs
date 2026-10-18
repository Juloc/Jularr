using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.System.Acquisition.V1
{
    public sealed class AcquisitionCycleParametersV1 : BaseParameters
    {
    }

    public sealed class DownloadJobParametersV1(long operationId) : BaseParameters
    {
        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; } = operationId;
    }
}
