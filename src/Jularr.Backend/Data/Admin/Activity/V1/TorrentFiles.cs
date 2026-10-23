using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    // Read-only file facts from the download client; never return filesystem write paths or credentials.
    public sealed class TorrentFileV1(string name, long sizeBytes, int progressPercent, bool isSelected) : BaseResponse
    {
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Name { get; } = name;

        [DataField(typeof(OperationFields), nameof(OperationFields.SizeBytes))]
        public long SizeBytes { get; } = sizeBytes;

        [DataField(typeof(OperationFields), nameof(OperationFields.ProgressPercent))]
        public int ProgressPercent { get; } = progressPercent;

        [DataField(typeof(OperationFields), nameof(OperationFields.CanPause))]
        public bool IsSelected { get; } = isSelected;
    }
}
