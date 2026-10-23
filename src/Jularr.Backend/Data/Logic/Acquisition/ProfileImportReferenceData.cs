using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class ProfileImportReferenceData : BaseData
    {
        public ProfileImportReferenceData(string sourceUrl, long sourceProfileId, MediaType mediaTypeId, int sourceMetadataProfileId = 0)
        {
            SourceMetadataProfileId = sourceMetadataProfileId;
            SourceUrl = sourceUrl;
            SourceProfileId = sourceProfileId;
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl), true)]
        public string SourceUrl { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long SourceProfileId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.SourceMetadataProfileId))]
        public int SourceMetadataProfileId { get; }
    }

    internal sealed class ProfileImportLinkData : BaseData
    {
        public ProfileImportLinkData(string sourceUrl, long sourceProfileId, long acquisitionProfileId, int sourceMetadataProfileId = 0)
        {
            SourceMetadataProfileId = sourceMetadataProfileId;
            SourceUrl = sourceUrl;
            SourceProfileId = sourceProfileId;
            AcquisitionProfileId = acquisitionProfileId;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.BaseUrl), true)]
        public string SourceUrl { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long SourceProfileId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long AcquisitionProfileId { get; }
        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.SourceMetadataProfileId))]
        public int SourceMetadataProfileId { get; }
    }
}
