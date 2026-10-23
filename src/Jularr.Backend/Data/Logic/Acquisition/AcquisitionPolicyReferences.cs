using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionMediaScopeData : BaseData
    {
        public AcquisitionMediaScopeData(MediaType mediaTypeId)
        {
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }

    internal sealed class AcquisitionTagSetData : BaseData
    {
        public AcquisitionTagSetData(long[] tagIds)
        {
            TagIds = tagIds;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.TagIds))]
        public long[] TagIds { get; }
    }
    internal sealed class PolicyIndexerData : BaseData
    {
        public PolicyIndexerData(long id)
        {
            Id = id;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long Id { get; }
    }
}
