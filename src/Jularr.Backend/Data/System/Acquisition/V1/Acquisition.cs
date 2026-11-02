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

    // Whether the cycle got further, and whether any of Jularr's own torrent downloads exist (none: the worker waits for a grab).
    public sealed class TorrentCycleResultV1(bool progressed, bool hasJobs) : BaseResponse
    {
        public bool Progressed { get; } = progressed;
        public bool HasJobs { get; } = hasJobs;
    }
}
