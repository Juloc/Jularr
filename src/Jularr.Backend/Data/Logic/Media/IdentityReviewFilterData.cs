using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class IdentityReviewFilterData : BaseData
    {
        public IdentityReviewFilterData(MediaType[] mediaTypeIds, MediaSubtype[] mediaSubtypeIds)
        {
            MediaTypeIds = mediaTypeIds;
            MediaSubtypeIds = mediaSubtypeIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeIds))]
        public MediaType[] MediaTypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeIds))]
        public MediaSubtype[] MediaSubtypeIds { get; }
    }
}
