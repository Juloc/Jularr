using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class BrowserAssistanceDownloadData(long acquisitionConnectionId, string baseUrl, string attentionUrl) : BaseData
    {
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; } = acquisitionConnectionId;

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl))]
        public string BaseUrl { get; } = baseUrl;

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionUrl))]
        public string AttentionUrl { get; } = attentionUrl;
    }
}
