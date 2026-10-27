using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class QueueWorksSearchDataV1 : BaseData
    {
        public QueueWorksSearchDataV1(long[]? workIds = null, MediaType? mediaTypeId = null, bool? isMonitored = null, WorkListStatus? workListStatusId = null, long? libraryRootId = null, long? acquisitionProfileId = null, string? query = null)
        {
            WorkIds = workIds;
            MediaTypeId = mediaTypeId;
            IsMonitored = isMonitored;
            WorkListStatusId = workListStatusId;
            LibraryRootId = libraryRootId;
            AcquisitionProfileId = acquisitionProfileId;
            Query = query;
        }

        [DataField(typeof(SearchRunFields), nameof(SearchRunFields.WorkIds))]
        public long[]? WorkIds { get; }

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
    }

    public sealed class SearchRunQueuedV1 : BaseResponse
    {
        public SearchRunQueuedV1(long operationId, int targetCount)
        {
            OperationId = operationId;
            TargetCount = targetCount;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(SearchRunFields), nameof(SearchRunFields.TargetCount))]
        public int TargetCount { get; }
    }

    public sealed class SearchRunDetailsV1 : BaseResponse
    {
        public SearchRunDetailsV1(long operationId, int targetCount, long processedCount, OperationStatusType operationStatusTypeId, bool isCancelled)
        {
            OperationId = operationId;
            TargetCount = targetCount;
            ProcessedCount = processedCount;
            OperationStatusTypeId = operationStatusTypeId;
            IsCancelled = isCancelled;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(SearchRunFields), nameof(SearchRunFields.TargetCount))]
        public int TargetCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProcessedCount))]
        public long ProcessedCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationStatusTypeId))]
        public OperationStatusType OperationStatusTypeId { get; }

        [DataField(typeof(SearchRunFields), nameof(SearchRunFields.IsCancelled))]
        public bool IsCancelled { get; }
    }

    public sealed class ReadSearchRunParametersV1 : BaseParameters
    {
        public ReadSearchRunParametersV1(long? operationId = null)
        {
            OperationId = operationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id), false)]
        public long? OperationId { get; }
    }

    public sealed class SearchRunParametersV1 : BaseParameters
    {
        public SearchRunParametersV1(long operationId, int page = 1)
        {
            OperationId = operationId;
            Page = page;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }
        public int Page { get; }
    }
}
