using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Wanted.V1
{
    public sealed class ReadWantedParametersV1 : BaseParameters
    {
        public ReadWantedParametersV1(int page = 1, int pageSize = 25, WantedViewType wantedViewTypeId = WantedViewType.All)
        {
            Page = page;
            PageSize = pageSize;
            WantedViewTypeId = wantedViewTypeId;
        }

        public int Page { get; }
        public int PageSize { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.WantedViewTypeId))]
        public WantedViewType WantedViewTypeId { get; }
    }

    public sealed class UpdateWantedParametersV1 : BaseParameters
    {
        public UpdateWantedParametersV1(long wantedItemId)
        {
            WantedItemId = wantedItemId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }
    }

    public sealed class UpdateWantedDataV1 : BaseData
    {
        public UpdateWantedDataV1(bool isPaused, long version, long? acquisitionProfileId = null, long? libraryRootId = null, bool? monitorsNewUnits = null, long[]? tagIds = null)
        {
            TagIds = tagIds;
            MonitorsNewUnits = monitorsNewUnits;
            IsPaused = isPaused;
            Version = version;
            AcquisitionProfileId = acquisitionProfileId;
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        // Without a choice the first library of the subtype, then of the type, receives imports.
        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        // Missing keeps the current choice.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.MonitorsNewUnits), false)]
        public bool? MonitorsNewUnits { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Version))]
        public long Version { get; }
        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.TagIds), false)]
        public long[]? TagIds { get; }
    }

    public sealed class WantedDetailsV1 : BaseResponse
    {
        public WantedDetailsV1(long id, long workId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, string? platformName, bool isPaused, long version, string[] requestedLanguages, bool hasMedia,
            bool isDownloading, int? progressPercent, DateTime? lastSearchedAt, string? outcomeKey, long? acquisitionProfileId, long? libraryRootId, bool monitorsNewUnits, bool hasUnits, long[]? tagIds = null)
        {
            TagIds = tagIds ?? [];
            HasUnits = hasUnits;
            MonitorsNewUnits = monitorsNewUnits;
            LibraryRootId = libraryRootId;
            PlatformName = platformName;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Id = id;
            WorkId = workId;
            CanonicalTitle = canonicalTitle;
            IsPaused = isPaused;
            Version = version;
            RequestedLanguages = requestedLanguages;
            HasMedia = hasMedia;
            IsDownloading = isDownloading;
            ProgressPercent = progressPercent;
            LastSearchedAt = lastSearchedAt;
            OutcomeKey = outcomeKey;
            AcquisitionProfileId = acquisitionProfileId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        // The platform a game is wanted for.
        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Name), false)]
        public string? PlatformName { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool IsPaused { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Version))]
        public long Version { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.RequestedLanguages))]
        public string[] RequestedLanguages { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.HasMedia))]
        public bool HasMedia { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsDownloading))]
        public bool IsDownloading { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.LastSearchedAt))]
        public DateTime? LastSearchedAt { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.OutcomeKey))]
        public string? OutcomeKey { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.MonitorsNewUnits))]
        public bool MonitorsNewUnits { get; }
        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.TagIds))]
        public long[] TagIds { get; }

        // Series and Manga are monitored unit by unit.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.HasUnits))]
        public bool HasUnits { get; }
    }

    // A season, episode or volume of the wanted Work and whether it is searched.
    public sealed class WantedUnitV1 : BaseResponse
    {
        public WantedUnitV1(long id, long? parentWorkUnitId, WorkUnitType workUnitTypeId, decimal number, string? title, DateOnly? releasedOn, bool isMonitored, bool hasMedia,
            int episodes, int monitoredEpisodes)
        {
            Episodes = episodes;
            MonitoredEpisodes = monitoredEpisodes;
            Id = id;
            ParentWorkUnitId = parentWorkUnitId;
            WorkUnitTypeId = workUnitTypeId;
            Number = number;
            Title = title;
            ReleasedOn = releasedOn;
            IsMonitored = isMonitored;
            HasMedia = hasMedia;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId))]
        public long Id { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId), false)]
        public long? ParentWorkUnitId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId))]
        public WorkUnitType WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber))]
        public decimal Number { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitTitle))]
        public string? Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleasedOn))]
        public DateOnly? ReleasedOn { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.IsMonitored))]
        public bool IsMonitored { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.HasMedia))]
        public bool HasMedia { get; }

        // A season's episodes and how many of them are monitored.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.SearchCount))]
        public int Episodes { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.SearchCount))]
        public int MonitoredEpisodes { get; }
    }

    // Seasons and volumes, plus one season's episodes when it is named.
    public sealed class ReadWantedUnitsParametersV1 : BaseParameters
    {
        public ReadWantedUnitsParametersV1(long wantedItemId, long? seasonId = null)
        {
            WantedItemId = wantedItemId;
            SeasonId = seasonId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId), false)]
        public long? SeasonId { get; }
    }

    public sealed class WantedUnitParametersV1 : BaseParameters
    {
        public WantedUnitParametersV1(long wantedItemId, long workUnitId)
        {
            WantedItemId = wantedItemId;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId))]
        public long WorkUnitId { get; }
    }

    // A season applies to its episodes.
    public sealed class UpdateWantedUnitDataV1 : BaseData
    {
        public UpdateWantedUnitDataV1(bool isMonitored)
        {
            IsMonitored = isMonitored;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.IsMonitored))]
        public bool IsMonitored { get; }
    }

    public sealed class WantedLibraryV1 : BaseResponse
    {
        public WantedLibraryV1(long id, string name)
        {
            Id = id;
            Name = name;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }
    }

    public sealed class WantedItemParametersV1 : BaseParameters
    {
        public WantedItemParametersV1(long wantedItemId)
        {
            WantedItemId = wantedItemId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }
    }

    public sealed class WantedReleaseCandidateV1 : BaseResponse
    {
        public WantedReleaseCandidateV1(string candidateId, string title, string connectionName, long? sizeBytes, DateTime? publishedAt, string? qualityKey,
            ReleaseDecisionType releaseDecisionTypeId, int score, string reasonCode, string? reasonValue)
        {
            CandidateId = candidateId;
            Title = title;
            ConnectionName = connectionName;
            SizeBytes = sizeBytes;
            PublishedAt = publishedAt;
            QualityKey = qualityKey;
            ReleaseDecisionTypeId = releaseDecisionTypeId;
            Score = score;
            ReasonCode = reasonCode;
            ReasonValue = reasonValue;
        }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.CandidateId))]
        public string CandidateId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string ConnectionName { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.SizeBytes))]
        public long? SizeBytes { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.PublishedAt))]
        public DateTime? PublishedAt { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReleaseDecisionTypeId))]
        public ReleaseDecisionType ReleaseDecisionTypeId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Score))]
        public int Score { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReasonCode))]
        public string ReasonCode { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReasonValue))]
        public string? ReasonValue { get; }
    }

    public sealed class SearchWantedDataV1 : BaseData
    {
        public SearchWantedDataV1(int? parentUnitNumber = null, int? unitNumber = null)
        {
            ParentUnitNumber = parentUnitNumber;
            UnitNumber = unitNumber;
        }

        // A season, optionally with its episode, or a volume.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber))]
        public int? ParentUnitNumber { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart))]
        public int? UnitNumber { get; }
    }

    public sealed class GrabWantedReleaseDataV1 : BaseData
    {
        public GrabWantedReleaseDataV1(string candidateId)
        {
            CandidateId = candidateId;
        }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.CandidateId))]
        public string CandidateId { get; }
    }

    public sealed class WantedReleaseV1 : BaseResponse
    {
        public WantedReleaseV1(long id, string title, string? connectionName, string? qualityKey, AcquisitionReleaseStatusType acquisitionReleaseStatusTypeId, string? reasonKey,
            int? progressPercent, DateTime createdAt, bool isBlocked)
        {
            Id = id;
            Title = title;
            ConnectionName = connectionName;
            QualityKey = qualityKey;
            AcquisitionReleaseStatusTypeId = acquisitionReleaseStatusTypeId;
            ReasonKey = reasonKey;
            ProgressPercent = progressPercent;
            CreatedAt = createdAt;
            IsBlocked = isBlocked;
        }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? ConnectionName { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.AcquisitionReleaseStatusTypeId))]
        public AcquisitionReleaseStatusType AcquisitionReleaseStatusTypeId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReasonKey))]
        public string? ReasonKey { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.IsBlocked))]
        public bool IsBlocked { get; }
    }

    public sealed class WantedReleaseParametersV1 : BaseParameters
    {
        public WantedReleaseParametersV1(long wantedItemId, long acquisitionReleaseId)
        {
            WantedItemId = wantedItemId;
            AcquisitionReleaseId = acquisitionReleaseId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Id))]
        public long AcquisitionReleaseId { get; }
    }

    public sealed class MarkWantedReleaseDataV1 : BaseData
    {
        public MarkWantedReleaseDataV1(AcquisitionReleaseStatusType acquisitionReleaseStatusTypeId)
        {
            AcquisitionReleaseStatusTypeId = acquisitionReleaseStatusTypeId;
        }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.AcquisitionReleaseStatusTypeId))]
        public AcquisitionReleaseStatusType AcquisitionReleaseStatusTypeId { get; }
    }

    // How a search now ended: the release taken, or why none was.
    public sealed class AutomaticSearchResultV1 : BaseResponse
    {
        public AutomaticSearchResultV1(string outcomeKey, string? title)
        {
            OutcomeKey = outcomeKey;
            Title = title;
        }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReasonKey))]
        public string OutcomeKey { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title), false)]
        public string? Title { get; }
    }
}
