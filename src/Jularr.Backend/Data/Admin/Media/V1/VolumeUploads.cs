using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class UploadVolumeDataV1 : BaseData
    {
        public UploadVolumeDataV1(UploadedFile file, decimal? volumeNumber)
        {
            File = file;
            VolumeNumber = volumeNumber;
        }

        [DataField(typeof(UploadFields), nameof(UploadFields.Volume))]
        public UploadedFile File { get; }

        // None names the whole series in one file.
        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? VolumeNumber { get; }
    }
}
