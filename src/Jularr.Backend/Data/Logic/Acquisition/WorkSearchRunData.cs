using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class SearchRunActionData : BaseData
    {
        public SearchRunActionData(long operationId, long accountId)
        {
            OperationId = operationId;
            AccountId = accountId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }
    }

    internal sealed class WorkSearchSelectionData : BaseData
    {
        public WorkSearchSelectionData(long[]? workIds, MediaType? mediaTypeId, bool? isMonitored, WorkListStatus? workListStatusId, long? libraryRootId, long? acquisitionProfileId, string? query, long? accountId, string selectionKey)
        {
            WorkIds = workIds;
            MediaTypeId = mediaTypeId;
            IsMonitored = isMonitored;
            WorkListStatusId = workListStatusId;
            LibraryRootId = libraryRootId;
            AcquisitionProfileId = acquisitionProfileId;
            Query = query;
            AccountId = accountId;
            SelectionKey = selectionKey;
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

        [DataField(typeof(AccountFields), nameof(AccountFields.Id), false)]
        public long? AccountId { get; }

        [DataField(typeof(SearchRunFields), nameof(SearchRunFields.SelectionKey))]
        public string SelectionKey { get; }
    }

    internal sealed class SearchRunProgressData : BaseData
    {
        public SearchRunProgressData(long operationId, long wantedItemId, string? outcomeKey)
        {
            OperationId = operationId;
            WantedItemId = wantedItemId;
            OutcomeKey = outcomeKey;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(SearchRunFields), nameof(SearchRunFields.OutcomeKey))]
        public string? OutcomeKey { get; }
    }

    internal sealed class SearchRunKeyData : BaseData
    {
        public SearchRunKeyData(long operationId)
        {
            OperationId = operationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }
    }

    internal sealed class SearchRunItemData : BaseData
    {
        public SearchRunItemData(long wantedItemId, long accountId, AccountRoleType accountRoleTypeId, bool isEnabled)
        {
            WantedItemId = wantedItemId;
            AccountId = accountId;
            AccountRoleTypeId = accountRoleTypeId;
            IsEnabled = isEnabled;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }
        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }
        [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
        public AccountRoleType AccountRoleTypeId { get; }
        [DataField(typeof(AccountFields), nameof(AccountFields.IsEnabled))]
        public bool IsEnabled { get; }
    }

    internal sealed class SearchRunStateData : BaseData
    {
        public SearchRunStateData(long operationId, int targetCount, long processedCount, OperationStatusType operationStatusTypeId, bool isCancelled, string selectionKey)
        {
            OperationId = operationId;
            TargetCount = targetCount;
            ProcessedCount = processedCount;
            OperationStatusTypeId = operationStatusTypeId;
            IsCancelled = isCancelled;
            SelectionKey = selectionKey;
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

        [DataField(typeof(SearchRunFields), nameof(SearchRunFields.SelectionKey))]
        public string SelectionKey { get; }
    }
}
