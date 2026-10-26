using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Browser.V1
{
    public sealed class BrowserLeaseParametersV1(Guid leaseId) : BaseParameters
    {
        [DataField(typeof(BrowserFields), nameof(BrowserFields.LeaseId))]
        public Guid LeaseId { get; } = leaseId;
    }

    public sealed class BrowserViewV1(Guid leaseId, int width, int height, long acquisitionConnectionId, bool isActive) : BaseResponse
    {
        [DataField(typeof(BrowserFields), nameof(BrowserFields.LeaseId))]
        public Guid LeaseId { get; } = leaseId;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.Width))]
        public int Width { get; } = width;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.Height))]
        public int Height { get; } = height;
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; } = acquisitionConnectionId;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.IsActive))]
        public bool IsActive { get; } = isActive;
    }

    public sealed class BrowserInputDataV1(string action, int x = 0, int y = 0, int delta = 0, string? key = null, string? text = null) : BaseData
    {
        [DataField(typeof(BrowserFields), nameof(BrowserFields.Action))]
        public string Action { get; } = action;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.X))]
        public int X { get; } = x;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.Y))]
        public int Y { get; } = y;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.Delta))]
        public int Delta { get; } = delta;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.Key))]
        public string? Key { get; } = key;
        [DataField(typeof(BrowserFields), nameof(BrowserFields.Text))]
        public string? Text { get; } = text;
    }

    public sealed class BrowserFrameV1(byte[] bytes) : BaseResponse, IFileOutput
    {
        [JsonIgnore]
        public Stream Content { get; } = new MemoryStream(bytes, writable: false);
        [JsonIgnore]
        public string ContentType => "image/jpeg";
    }
}
