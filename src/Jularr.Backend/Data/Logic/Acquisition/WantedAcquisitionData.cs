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
            string? profileName, string? definition, string[] releaseIdentities, bool isPaused, bool hasMedia, bool isDownloading, int searchCount, string[] externalIdentities,
            string[] requestedLocales, string? author)
        {
            Author = author;
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
            ExternalIdentities = externalIdentities;
            RequestedLocales = requestedLocales;
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

        // Provider mappings as namespace:value pairs.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalIdentityList))]
        public string[] ExternalIdentities { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.RequestedLanguages))]
        public string[] RequestedLocales { get; }

        // Book authors confirm a release; empty for other media.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value), false)]
        public string? Author { get; }
    }

    internal sealed class EpisodeStateData : BaseData
    {
        public EpisodeStateData(long workUnitId, int seasonNumber, int episodeNumber, bool isAired, bool hasMedia, bool isCovered)
        {
            WorkUnitId = workUnitId;
            SeasonNumber = seasonNumber;
            EpisodeNumber = episodeNumber;
            IsAired = isAired;
            HasMedia = hasMedia;
            IsCovered = isCovered;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId))]
        public long WorkUnitId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.SeasonNumber), true)]
        public int SeasonNumber { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.EpisodeStart), true)]
        public int EpisodeNumber { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsAired))]
        public bool IsAired { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.HasMedia))]
        public bool HasMedia { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsCovered))]
        public bool IsCovered { get; }

        public bool IsMissing => IsAired && !HasMedia && !IsCovered;
    }

    internal sealed class WantedCompletionData : BaseData
    {
        public WantedCompletionData(long wantedItemId, bool isComplete)
        {
            WantedItemId = wantedItemId;
            IsComplete = isComplete;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsComplete))]
        public bool IsComplete { get; }
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
        public AcquisitionGrabData(long wantedItemId, long indexerConnectionId, string releaseIdentity, string title, string? qualityKey, long downloadConnectionId, int? seasonNumber,
            int? episodeStart, int? episodeEnd, string? sourceUrl = null)
        {
            SourceUrl = sourceUrl;
            SeasonNumber = seasonNumber;
            EpisodeStart = episodeStart;
            EpisodeEnd = episodeEnd;
            WantedItemId = wantedItemId;
            IndexerConnectionId = indexerConnectionId;
            ReleaseIdentity = releaseIdentity;
            Title = title;
            QualityKey = qualityKey;
            DownloadConnectionId = downloadConnectionId;
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

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.SeasonNumber))]
        public int? SeasonNumber { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.EpisodeStart))]
        public int? EpisodeStart { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.EpisodeEnd))]
        public int? EpisodeEnd { get; }

        // Where a direct source serves the file.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.SourceUrl))]
        public string? SourceUrl { get; }
    }

    internal sealed class DownloadStartData : BaseData
    {
        public DownloadStartData(long operationId, string externalId)
        {
            OperationId = operationId;
            ExternalId = externalId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ExternalId), true)]
        public string ExternalId { get; }
    }

    internal sealed class DownloadData : BaseData
    {
        public DownloadData(long operationId, long wantedItemId, long workId, MediaType mediaTypeId, string canonicalTitle, int? releaseYear, long acquisitionConnectionId,
            string? externalId, string title, DateTime startedAt, string? qualityKey, string? author, string? sourceUrl)
        {
            SourceUrl = sourceUrl;
            Author = author;
            QualityKey = qualityKey;
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

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ExternalId))]
        public string? ExternalId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.StartedAt), true)]
        public DateTime StartedAt { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value), false)]
        public string? Author { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.SourceUrl))]
        public string? SourceUrl { get; }
    }

    internal sealed class DownloadProgressData : BaseData
    {
        public DownloadProgressData(string progress)
        {
            Progress = progress;
        }

        // JSON rows of OperationId, ProgressPercent, SizeBytes, RemainingBytes, EtaSeconds and DownloadStateTypeId.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Progress))]
        public string Progress { get; }
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

    internal sealed class DownloadAttentionData : BaseData
    {
        public DownloadAttentionData(long operationId, string? attentionKey)
        {
            OperationId = operationId;
            AttentionKey = attentionKey;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionKey))]
        public string? AttentionKey { get; }
    }

    internal sealed class DownloadFinishedData : BaseData
    {
        public DownloadFinishedData(bool isFinished)
        {
            IsFinished = isFinished;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.IsFinished))]
        public bool IsFinished { get; }
    }
}
