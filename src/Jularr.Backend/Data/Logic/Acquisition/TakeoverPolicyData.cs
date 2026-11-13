using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // The tags a taken-over title carried there, as Jularr's tags.
    internal sealed class TakeoverWorkTagsData : BaseData
    {
        public TakeoverWorkTagsData(long workId, long[] tagIds)
        {
            WorkId = workId;
            TagIds = tagIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.TagIds))]
        public long[] TagIds { get; }
    }
}
