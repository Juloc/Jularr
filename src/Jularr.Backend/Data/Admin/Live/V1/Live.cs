using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Live;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Live.V1
{
    public sealed class ReadLiveAdminParametersV1 : BaseParameters
    {
    }

    // What an administrator watches: every running or just finished operation, the targets they download for and the requests that wait for them.
    public sealed class LiveAdminV1 : BaseResponse
    {
        public LiveAdminV1(BaseDataList<LiveAdminRequestV1> requests, BaseDataList<LiveOperationV1> operations, BaseDataList<LiveWantedV1> wanted, BaseDataList<LiveUnitV1> units)
        {
            Requests = requests;
            Operations = operations;
            Wanted = wanted;
            Units = units;
        }

        public BaseDataList<LiveAdminRequestV1> Requests { get; }
        public BaseDataList<LiveOperationV1> Operations { get; }
        public BaseDataList<LiveWantedV1> Wanted { get; }

        // The units (episodes, volumes, tracks) that are searched or downloaded now; none while nothing runs.
        public BaseDataList<LiveUnitV1> Units { get; }
    }

    // One unit of a Work that is searched for, downloaded or imported right now: how far it is, and how many units its season has (the season's sum says "3 of 12").
    public sealed class LiveUnitV1 : BaseResponse
    {
        public LiveUnitV1(long workUnitId, long? parentWorkUnitId, int seasonUnitCount, AcquisitionRequestStatusType acquisitionRequestStatusTypeId, int? progressPercent)
        {
            WorkUnitId = workUnitId;
            ParentWorkUnitId = parentWorkUnitId;
            SeasonUnitCount = seasonUnitCount;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            ProgressPercent = progressPercent;
        }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id), false)]
        public long? ParentWorkUnitId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.EpisodeCount))]
        public int SeasonUnitCount { get; }

        // Searching, Downloading or Importing.
        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProgressPercent))]
        public int? ProgressPercent { get; }
    }

    // A request of any profile with its phase and, while it downloads, speed, time left, release, indexer, client and the reason it waits or failed.
    public sealed class LiveAdminRequestV1 : BaseResponse
    {
        public LiveAdminRequestV1(long id, long workId, AcquisitionRequestStatusType acquisitionRequestStatusTypeId, int? progressPercent, int? etaSeconds, long? bytesPerSecond,
            string? releaseTitle, string? indexerName, string? clientName, string? reasonKey)
        {
            Id = id;
            WorkId = workId;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            ProgressPercent = progressPercent;
            EtaSeconds = etaSeconds;
            BytesPerSecond = bytesPerSecond;
            ReleaseTitle = releaseTitle;
            IndexerName = indexerName;
            ClientName = clientName;
            ReasonKey = reasonKey;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.EtaSeconds))]
        public int? EtaSeconds { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.BytesPerSecond))]
        public long? BytesPerSecond { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title), false)]
        public string? ReleaseTitle { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? IndexerName { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? ClientName { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey))]
        public string? ReasonKey { get; }
    }

    // One download, scan or backup as the Queue shows it.
    public sealed class LiveOperationV1 : BaseResponse
    {
        public LiveOperationV1(long id, OperationType operationTypeId, OperationStatusType operationStatusTypeId, DownloadStateType? downloadStateTypeId, bool isWaiting, int? progressPercent,
            long? sizeBytes, long? remainingBytes, int? etaSeconds, long? bytesPerSecond, string? reasonKey)
        {
            Id = id;
            OperationTypeId = operationTypeId;
            OperationStatusTypeId = operationStatusTypeId;
            DownloadStateTypeId = downloadStateTypeId;
            IsWaiting = isWaiting;
            ProgressPercent = progressPercent;
            SizeBytes = sizeBytes;
            RemainingBytes = remainingBytes;
            EtaSeconds = etaSeconds;
            BytesPerSecond = bytesPerSecond;
            ReasonKey = reasonKey;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long Id { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationTypeId))]
        public OperationType OperationTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationStatusTypeId))]
        public OperationStatusType OperationStatusTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadStateTypeId), false)]
        public DownloadStateType? DownloadStateTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.IsWaiting))]
        public bool IsWaiting { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.SizeBytes))]
        public long? SizeBytes { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.RemainingBytes))]
        public long? RemainingBytes { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.EtaSeconds))]
        public int? EtaSeconds { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.BytesPerSecond))]
        public long? BytesPerSecond { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey))]
        public string? ReasonKey { get; }
    }

    // The downloads of one target together: where a title's progress shows on its page.
    public sealed class LiveWantedV1 : BaseResponse
    {
        public LiveWantedV1(long id, long workId, AcquisitionRequestStatusType acquisitionRequestStatusTypeId, int downloadCount, bool isWaiting, bool isPaused, int? progressPercent,
            long? sizeBytes, long? remainingBytes, int? etaSeconds, long? bytesPerSecond, string? releaseTitle, string? indexerName, string? clientName)
        {
            Id = id;
            WorkId = workId;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            DownloadCount = downloadCount;
            IsWaiting = isWaiting;
            IsPaused = isPaused;
            ProgressPercent = progressPercent;
            SizeBytes = sizeBytes;
            RemainingBytes = remainingBytes;
            EtaSeconds = etaSeconds;
            BytesPerSecond = bytesPerSecond;
            ReleaseTitle = releaseTitle;
            IndexerName = indexerName;
            ClientName = clientName;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.DownloadCount))]
        public int DownloadCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.IsWaiting))]
        public bool IsWaiting { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.SizeBytes))]
        public long? SizeBytes { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.RemainingBytes))]
        public long? RemainingBytes { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.EtaSeconds))]
        public int? EtaSeconds { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.BytesPerSecond))]
        public long? BytesPerSecond { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title), false)]
        public string? ReleaseTitle { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? IndexerName { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? ClientName { get; }
    }
}
