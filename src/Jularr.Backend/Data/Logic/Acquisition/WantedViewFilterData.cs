using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class WantedViewFilterData : BaseData
    {
        public WantedViewFilterData(WantedViewType wantedViewTypeId, MediaType[] mediaTypeIds, MediaSubtype[] mediaSubtypeIds)
        {
            WantedViewTypeId = wantedViewTypeId;
            MediaTypeIds = mediaTypeIds;
            MediaSubtypeIds = mediaSubtypeIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeIds))]
        public MediaType[] MediaTypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeIds))]
        public MediaSubtype[] MediaSubtypeIds { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.WantedViewTypeId))]
        public WantedViewType WantedViewTypeId { get; }
    }
}
