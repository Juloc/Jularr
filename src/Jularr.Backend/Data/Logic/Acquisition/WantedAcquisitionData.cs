using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class WantedItemParameters : BaseData
    {
        public WantedItemParameters(long wantedItemId)
        {
            WantedItemId = wantedItemId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }
    }

    internal sealed class WantedTargetData : BaseData
    {
        public WantedTargetData(long wantedItemId, long workId, MediaType mediaTypeId, string canonicalTitle, int? releaseYear, string[] titles, long? acquisitionProfileId,
            string? profileName, string? definition, string[] releaseIdentities, bool isPaused, bool hasMedia, bool isDownloading, int searchCount)
        {
            WantedItemId = wantedItemId;
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Titles = titles;
            AcquisitionProfileId = acquisitionProfileId;
            ProfileName = profileName;
            Definition = definition;
            ReleaseIdentities = releaseIdentities;
            IsPaused = isPaused;
            HasMedia = hasMedia;
            IsDownloading = isDownloading;
            SearchCount = searchCount;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Titles))]
        public string[] Titles { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name), false)]
        public string? ProfileName { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition), false)]
        public string? Definition { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReleaseIdentities))]
        public string[] ReleaseIdentities { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.HasMedia))]
        public bool HasMedia { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsDownloading))]
        public bool IsDownloading { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.SearchCount))]
        public int SearchCount { get; }
    }

    internal sealed class WantedSearchOutcomeData : BaseData
    {
        public WantedSearchOutcomeData(long wantedItemId, string outcomeKey, DateTime? nextSearchAt)
        {
            WantedItemId = wantedItemId;
            OutcomeKey = outcomeKey;
            NextSearchAt = nextSearchAt;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.OutcomeKey), true)]
        public string OutcomeKey { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.NextSearchAt))]
        public DateTime? NextSearchAt { get; }
    }

    internal sealed class AcquisitionGrabData : BaseData
    {
        public AcquisitionGrabData(long wantedItemId, long indexerConnectionId, string releaseIdentity, string title, string? qualityKey, long downloadConnectionId, string externalId)
        {
            WantedItemId = wantedItemId;
            IndexerConnectionId = indexerConnectionId;
            ReleaseIdentity = releaseIdentity;
            Title = title;
            QualityKey = qualityKey;
            DownloadConnectionId = downloadConnectionId;
            ExternalId = externalId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long IndexerConnectionId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReleaseIdentity))]
        public string ReleaseIdentity { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long DownloadConnectionId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ExternalId), true)]
        public string ExternalId { get; }
    }

    internal sealed class DownloadData : BaseData
    {
        public DownloadData(long operationId, long wantedItemId, long workId, MediaType mediaTypeId, string canonicalTitle, int? releaseYear, long acquisitionConnectionId,
            string externalId, string title, DateTime startedAt)
        {
            OperationId = operationId;
            WantedItemId = wantedItemId;
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            AcquisitionConnectionId = acquisitionConnectionId;
            ExternalId = externalId;
            Title = title;
            StartedAt = startedAt;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ExternalId), true)]
        public string ExternalId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.StartedAt), true)]
        public DateTime StartedAt { get; }
    }

    internal sealed class DownloadProgressData : BaseData
    {
        public DownloadProgressData(long operationId, int? progressPercent)
        {
            OperationId = operationId;
            ProgressPercent = progressPercent;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ProgressPercent))]
        public int? ProgressPercent { get; }
    }

    internal sealed class DownloadFailureData : BaseData
    {
        public DownloadFailureData(long operationId, string reasonKey, DateTime nextSearchAt)
        {
            OperationId = operationId;
            ReasonKey = reasonKey;
            NextSearchAt = nextSearchAt;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReasonKey), true)]
        public string ReasonKey { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.NextSearchAt), true)]
        public DateTime NextSearchAt { get; }
    }

    internal sealed class DownloadOperationParameters : BaseData
    {
        public DownloadOperationParameters(long operationId)
        {
            OperationId = operationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }
    }

    internal sealed class LibraryFilePathData : BaseData
    {
        public LibraryFilePathData(long libraryRootId, string relativePath)
        {
            LibraryRootId = libraryRootId;
            RelativePath = relativePath;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }
    }

    internal sealed class WantedDueData : BaseData
    {
        public WantedDueData(DateTime now, int limit)
        {
            Now = now;
            Limit = limit;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.NextSearchAt), true)]
        public DateTime Now { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.SearchCount))]
        public int Limit { get; }
    }

    internal sealed class RequestProgressData : BaseData
    {
        public RequestProgressData(long wantedItemId, AcquisitionRequestStatusType acquisitionRequestStatusTypeId)
        {
            WantedItemId = wantedItemId;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }
    }
}
