using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Artwork.V1
{
    public sealed class ArtworkReadParametersV1 : BaseParameters
    {
        public ArtworkReadParametersV1(long workId, ArtworkSlot slotId, long? workUnitId = null)
        {
            WorkId = workId;
            SlotId = slotId;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SlotId))]
        public ArtworkSlot SlotId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id), false)]
        public long? WorkUnitId { get; }
    }

    public sealed class ArtworkUnitReadParametersV1 : BaseParameters
    {
        public ArtworkUnitReadParametersV1(long workId, long workUnitId, ArtworkSlot slotId)
        {
            WorkId = workId;
            WorkUnitId = workUnitId;
            SlotId = slotId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SlotId))]
        public ArtworkSlot SlotId { get; }
    }

    public sealed class ArtworkJxlImageV1 : BaseResponse, IBinaryOutput
    {
        public ArtworkJxlImageV1(byte[] bytes) => Bytes = bytes;

        [JsonIgnore]
        public byte[] Bytes { get; }

        [JsonIgnore]
        public string ContentType => "image/jxl";
    }

    public sealed class ArtworkImageV1 : BaseResponse, IBinaryOutput
    {
        public ArtworkImageV1(byte[] bytes)
        {
            Bytes = bytes;
        }

        [JsonIgnore]
        public byte[] Bytes { get; }

        [JsonIgnore]
        public string ContentType => "image/webp";
    }
}
