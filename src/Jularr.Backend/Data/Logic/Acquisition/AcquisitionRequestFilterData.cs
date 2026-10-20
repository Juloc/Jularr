using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionRequestFilterData : BaseData
    {
        public AcquisitionRequestFilterData(RequestTabType requestTabTypeId)
        {
            RequestTabTypeId = requestTabTypeId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestTabTypeId))]
        public RequestTabType RequestTabTypeId { get; }
    }

    internal sealed class ProfileRequestFilterData : BaseData
    {
        public ProfileRequestFilterData(RequestListFilterType requestListFilterTypeId, RequestSortType requestSortTypeId, MediaType? mediaTypeId, MediaSubtype? mediaSubtypeId)
        {
            RequestListFilterTypeId = requestListFilterTypeId;
            RequestSortTypeId = requestSortTypeId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestListFilterTypeId))]
        public RequestListFilterType RequestListFilterTypeId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestSortTypeId))]
        public RequestSortType RequestSortTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }
    }
}
