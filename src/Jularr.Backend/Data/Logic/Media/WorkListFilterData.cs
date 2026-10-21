using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // The filters and the order of the library list, without its paging.
    internal sealed class WorkListFilterData : BaseData
    {
        public WorkListFilterData(MediaType? mediaTypeId, bool? isMonitored, WorkListStatus? workListStatusId, long? libraryRootId, long? acquisitionProfileId, string? query, WorkSortType workSortTypeId,
            bool isDescending)
        {
            MediaTypeId = mediaTypeId;
            IsMonitored = isMonitored;
            WorkListStatusId = workListStatusId;
            LibraryRootId = libraryRootId;
            AcquisitionProfileId = acquisitionProfileId;
            Query = query;
            WorkSortTypeId = workSortTypeId;
            IsDescending = isDescending;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.IsMonitoredFilter))]
        public bool? IsMonitored { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkListStatusFilterId))]
        public WorkListStatus? WorkListStatusId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.Query))]
        public string? Query { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkSortTypeId))]
        public WorkSortType WorkSortTypeId { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.IsDescending))]
        public bool IsDescending { get; }
    }
}
