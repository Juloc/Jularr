using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;
using Jularr.Backend.Data.Fields.Profile;

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
        public WantedTargetData(long wantedItemId, long workId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear, string[] titles, long? acquisitionProfileId,
            string? profileName, string? definition, string[] releaseIdentities, bool isPaused, bool hasMedia, bool isDownloading, int searchCount, string[] externalIdentities,
            string[] requestedLocales, string? author, string? platformAbbreviation, int? platformIgdbId, string? installedFiles = null,
            string[]? editionTitles = null, string[]? isbns = null, string? originalLanguage = null, string? formats = null, string? qualitySizes = null,
            int? runtimeMinutes = null, string? releaseProfiles = null, string[]? monitoredEditionLanguages = null, string[]? unmonitoredEditionLanguages = null,
            string? releaseGroupRatings = null)
        {
            ReleaseGroupRatings = releaseGroupRatings ?? "{}";
            MonitoredEditionLanguages = monitoredEditionLanguages ?? [];
            UnmonitoredEditionLanguages = unmonitoredEditionLanguages ?? [];
            ReleaseProfiles = releaseProfiles ?? "[]";
            QualitySizes = qualitySizes ?? "[]";
            RuntimeMinutes = runtimeMinutes;
            Formats = formats ?? "[]";
            OriginalLanguage = originalLanguage;
            EditionTitles = editionTitles ?? [];
            Isbns = isbns ?? [];
            InstalledFiles = installedFiles ?? "[]";
            Author = author;
            PlatformAbbreviation = platformAbbreviation;
            PlatformIgdbId = platformIgdbId;
            WantedItemId = wantedItemId;
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
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

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

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

        // The platform a game is wanted for.
        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Abbreviation), false)]
        public string? PlatformAbbreviation { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.IgdbId), false)]
        public int? PlatformIgdbId { get; }

        // The Work's own present files, their quality and release name as JSON; an upgrade must beat the best of them.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string InstalledFiles { get; }

        // The titles and ISBNs of the editions in the requested languages.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.EditionTitles))]
        public string[] EditionTitles { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Isbns))]
        public string[] Isbns { get; }

        // The languages of the book's editions an administrator wants and those switched off.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.EditionLanguages))]
        public string[] MonitoredEditionLanguages { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.EditionLanguages))]
        public string[] UnmonitoredEditionLanguages { get; }

        // The language the Work was made in; an untagged book release is taken to be in it.
        [DataField(typeof(WorkFields), nameof(WorkFields.OriginalLanguage))]
        public string? OriginalLanguage { get; }

        // The custom formats of the media type as JSON.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string Formats { get; }

        // The media type's quality sizes as JSON and the minutes of a film or an episode.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string QualitySizes { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.RuntimeMinutes))]
        public int? RuntimeMinutes { get; }

        // The release profiles that apply to the title, as JSON.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string ReleaseProfiles { get; }

        // The rated release groups of the media type as a JSON object of name and rating.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Definition))]
        public string ReleaseGroupRatings { get; }
    }

    internal sealed class UnitStateData : BaseData
    {
        public UnitStateData(long workUnitId, int? parentNumber, int number, bool isAvailable, bool hasMedia, bool isCovered, int? absoluteNumber = null, DateOnly? releasedOn = null, bool needsUpgrade = false, string? title = null)
        {
            Title = title;
            NeedsUpgrade = needsUpgrade;
            AbsoluteNumber = absoluteNumber;
            ReleasedOn = releasedOn;
            WorkUnitId = workUnitId;
            ParentNumber = parentNumber;
            Number = number;
            IsAvailable = isAvailable;
            HasMedia = hasMedia;
            IsCovered = isCovered;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId))]
        public long WorkUnitId { get; }

        // The season of an episode; volumes have none.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber), false)]
        public int? ParentNumber { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart), true)]
        public int Number { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsAvailable))]
        public bool IsAvailable { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.HasMedia))]
        public bool HasMedia { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsCovered))]
        public bool IsCovered { get; }

        // The place of a regular episode across all seasons, as absolute releases count.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart), false)]
        public int? AbsoluteNumber { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleasedOn))]
        public DateOnly? ReleasedOn { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Title))]
        public string? Title { get; }

        public bool IsMissing => IsAvailable && !HasMedia && !IsCovered;
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.NeedsUpgrade))]
        public bool NeedsUpgrade { get; }
        public bool NeedsSearch => IsAvailable && (!HasMedia || NeedsUpgrade) && !IsCovered;
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
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.IndexerFlags))]
        public string[] IndexerFlags { get; init; } = [];
        public AcquisitionGrabData(long wantedItemId, long? indexerConnectionId, string releaseIdentity, string title, string? qualityKey, long downloadConnectionId, int? parentUnitNumber,
            int? unitStart, int? unitEnd, string? sourceUrl = null, long? requestedByAccountId = null)
        {
            RequestedByAccountId = requestedByAccountId;
            SourceUrl = sourceUrl;
            ParentUnitNumber = parentUnitNumber;
            UnitStart = unitStart;
            UnitEnd = unitEnd;
            WantedItemId = wantedItemId;
            IndexerConnectionId = indexerConnectionId;
            ReleaseIdentity = releaseIdentity;
            Title = title;
            QualityKey = qualityKey;
            DownloadConnectionId = downloadConnectionId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        // None for a download a person added by hand.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? IndexerConnectionId { get; }

        // The administrator who took it by hand; automatic grabs have none.
        [DataField(typeof(Fields.Account.AccountFields), nameof(Fields.Account.AccountFields.Id), false)]
        public long? RequestedByAccountId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReleaseIdentity))]
        public string ReleaseIdentity { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long DownloadConnectionId { get; }

        // The season and episode range, or the volume range, a release claims.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber))]
        public int? ParentUnitNumber { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart))]
        public int? UnitStart { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitEnd))]
        public int? UnitEnd { get; }

        // Where a direct source serves the file.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.SourceUrl))]
        public string? SourceUrl { get; }

        // An automatic grab must still be monitored when its reservation is made.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused))]
        public bool RequiresActiveMonitoring { get; init; }
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

    internal sealed class OperationIdData(long operationId) : BaseData
    {
        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; } = operationId;
    }

    internal sealed class NewsServerIdData(long acquisitionConnectionId, int? maxConnections) : BaseData
    {
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; } = acquisitionConnectionId;

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.MaxConnections))]
        public int? MaxConnections { get; } = maxConnections;
    }

    internal sealed class DownloadLookupData(long? operationId) : BaseData
    {
        [DataField(typeof(OperationFields), nameof(OperationFields.Id), false)]
        public long? OperationId { get; } = operationId;
    }

    internal sealed class DownloadData : BaseData
    {
        public DownloadData(long operationId, long wantedItemId, long workId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear, long acquisitionConnectionId,
            string? externalId, string title, DateTime startedAt, string? qualityKey, string? author, string? sourceUrl,
            string? sourceLinkUrl, int trackCount, long? gamePlatformId, string? platformName, DateTime? downloadWaitUntil, int? expectedParentUnitNumber = null, int? expectedUnitStart = null, int? expectedUnitEnd = null,
            bool isPaused = false)
        {
            DownloadWaitUntil = downloadWaitUntil;
            IsPaused = isPaused;
            ExpectedParentUnitNumber = expectedParentUnitNumber;
            ExpectedUnitStart = expectedUnitStart;
            ExpectedUnitEnd = expectedUnitEnd;
            GamePlatformId = gamePlatformId;
            PlatformName = platformName;
            SourceUrl = sourceUrl;
            SourceLinkUrl = sourceLinkUrl;
            TrackCount = trackCount;
            Author = author;
            QualityKey = qualityKey;
            OperationId = operationId;
            WantedItemId = wantedItemId;
            WorkId = workId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
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

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

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

        [DataField(typeof(OperationFields), nameof(OperationFields.SourceLinkUrl))]
        public string? SourceLinkUrl { get; }

        // Set while a direct source asks Jularr to wait before its file host is contacted.
        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadWaitUntil), false)]
        public DateTime? DownloadWaitUntil { get; }

        // A download told to wait is not run.
        [DataField(typeof(OperationFields), nameof(OperationFields.IsPaused))]
        public bool IsPaused { get; }

        // Tracks the album's edition names; files are tied to them only when the counts agree.
        [DataField(typeof(WorkFields), nameof(WorkFields.TrackCount))]
        public int TrackCount { get; }

        // The platform a game download is for; its files land below it.
        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id), false)]
        public long? GamePlatformId { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Name), false)]
        public string? PlatformName { get; }

        // The season and episodes the grab was made for; none for a whole season or a film.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber))]
        public int? ExpectedParentUnitNumber { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart))]
        public int? ExpectedUnitStart { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitEnd))]
        public int? ExpectedUnitEnd { get; }
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

    internal sealed class StoredFolderData : BaseData
    {
        public StoredFolderData(long libraryRootId, string folders)
        {
            LibraryRootId = libraryRootId;
            Folders = folders;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        // Folders below the root as a JSON array; an empty one is the root itself.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Folders { get; }
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
        public DownloadAttentionData(long operationId, string? attentionKey, string? attentionUrl, string? attentionProposal = null)
        {
            OperationId = operationId;
            AttentionKey = attentionKey;
            AttentionUrl = attentionUrl;
            AttentionProposal = attentionProposal;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionKey))]
        public string? AttentionKey { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionUrl))]
        public string? AttentionUrl { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionProposal))]
        public string? AttentionProposal { get; }
    }

    internal sealed class DownloadHintData : BaseData
    {
        public DownloadHintData(long operationId, string? hintKey)
        {
            OperationId = operationId;
            HintKey = hintKey;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.HintKey))]
        public string? HintKey { get; }
    }

    // What a waiting download keeps after some of its files were imported by hand.
    internal sealed class ManualImportProgressData : BaseData
    {
        public ManualImportProgressData(long operationId, string attentionProposal)
        {
            OperationId = operationId;
            AttentionProposal = attentionProposal;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionProposal))]
        public string AttentionProposal { get; }
    }

    internal sealed class ActivityOperationData : BaseData
    {
        public ActivityOperationData(long operationId)
        {
            OperationId = operationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }
    }

    // The download an action is about, whatever its client.
    // A download that waits for a person and knows where its files are.
    internal sealed class ManualImportDownloadData : BaseData
    {
        public ManualImportDownloadData(long operationId, long wantedItemId, long acquisitionConnectionId, string? externalId, string attentionProposal)
        {
            OperationId = operationId;
            WantedItemId = wantedItemId;
            AcquisitionConnectionId = acquisitionConnectionId;
            ExternalId = externalId;
            AttentionProposal = attentionProposal;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ExternalId))]
        public string? ExternalId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionProposal))]
        public string AttentionProposal { get; }
    }

    internal sealed class DownloadActionData : BaseData
    {
        public DownloadActionData(long operationId, long acquisitionConnectionId, string? externalId, AcquisitionConnectionType acquisitionConnectionTypeId, string? attentionKey, bool hasProposal,
            DownloadStateType? downloadStateTypeId, DownloadPriorityType downloadPriorityTypeId = DownloadPriorityType.Normal, int? queuePosition = null)
        {
            DownloadPriorityTypeId = downloadPriorityTypeId;
            QueuePosition = queuePosition;
            OperationId = operationId;
            AcquisitionConnectionId = acquisitionConnectionId;
            ExternalId = externalId;
            AcquisitionConnectionTypeId = acquisitionConnectionTypeId;
            AttentionKey = attentionKey;
            HasProposal = hasProposal;
            DownloadStateTypeId = downloadStateTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ExternalId))]
        public string? ExternalId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.AcquisitionConnectionTypeId))]
        public AcquisitionConnectionType AcquisitionConnectionTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionKey))]
        public string? AttentionKey { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.HasProposal))]
        public bool HasProposal { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadStateTypeId))]
        public DownloadStateType? DownloadStateTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadPriorityTypeId))]
        public DownloadPriorityType DownloadPriorityTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueuePosition))]
        public int? QueuePosition { get; }
    }

    // One client's part of the queue: a kind, and for SABnzbd and qBittorrent its connection.
    internal sealed class DownloadQueueScopeData : BaseData
    {
        public DownloadQueueScopeData(DownloadClientKind? downloadClientKindId, long? acquisitionConnectionId)
        {
            DownloadClientKindId = downloadClientKindId;
            AcquisitionConnectionId = acquisitionConnectionId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId), false)]
        public DownloadClientKind? DownloadClientKindId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }
    }

    // The downloads of a client in the order it takes them.
    internal sealed class DownloadQueueEntryData : BaseData
    {
        public DownloadQueueEntryData(long operationId, DownloadPriorityType downloadPriorityTypeId, int? queuePosition)
        {
            OperationId = operationId;
            DownloadPriorityTypeId = downloadPriorityTypeId;
            QueuePosition = queuePosition;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadPriorityTypeId))]
        public DownloadPriorityType DownloadPriorityTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueuePosition))]
        public int? QueuePosition { get; }
    }

    // A client's new order: every download in its place, from 0.
    internal sealed class DownloadQueueOrderData : BaseData
    {
        public DownloadQueueOrderData(long[] operationIds)
        {
            OperationIds = operationIds;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueOrder))]
        public long[] OperationIds { get; }
    }

    internal sealed class DownloadPriorityData : BaseData
    {
        public DownloadPriorityData(long[] operationIds, DownloadPriorityType downloadPriorityTypeId)
        {
            OperationIds = operationIds;
            DownloadPriorityTypeId = downloadPriorityTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueOrder))]
        public long[] OperationIds { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadPriorityTypeId))]
        public DownloadPriorityType DownloadPriorityTypeId { get; }
    }

    // Finished downloads leaving the Downloads page's history: the chosen ones, or every completed one of a client (or of all).
    internal sealed class DownloadHistoryDeletionData : BaseData
    {
        public DownloadHistoryDeletionData(long[]? operationIds, DownloadClientKind? downloadClientKindId = null, long? acquisitionConnectionId = null)
        {
            OperationIds = operationIds;
            DownloadClientKindId = downloadClientKindId;
            AcquisitionConnectionId = acquisitionConnectionId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Ids), false)]
        public long[]? OperationIds { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId), false)]
        public DownloadClientKind? DownloadClientKindId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }
    }

    // A download taken out of the queue: its release may be taken again or never, and the target is searched from this time on.
    internal sealed class DownloadRemovalData : BaseData
    {
        public DownloadRemovalData(long operationId, bool blocksRelease, DateTime nextSearchAt)
        {
            OperationId = operationId;
            BlocksRelease = blocksRelease;
            NextSearchAt = nextSearchAt;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.BlocksRelease))]
        public bool BlocksRelease { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.NextSearchAt))]
        public DateTime NextSearchAt { get; }
    }

    internal sealed class DownloadPauseData : BaseData
    {
        public DownloadPauseData(long operationId, bool isPaused)
        {
            OperationId = operationId;
            IsPaused = isPaused;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.IsPaused))]
        public bool IsPaused { get; }
    }

    internal sealed class DownloadWaitData : BaseData
    {
        public DownloadWaitData(long operationId, DateTime downloadWaitUntil, int etaSeconds)
        {
            OperationId = operationId;
            DownloadWaitUntil = downloadWaitUntil;
            EtaSeconds = etaSeconds;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        // When the source's own countdown ends; the monitor resumes the job then.
        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadWaitUntil), true)]
        public DateTime DownloadWaitUntil { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.EtaSeconds))]
        public int EtaSeconds { get; }
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

    internal sealed class SourceLinkData : BaseData
    {
        public SourceLinkData(long operationId, string sourceLinkUrl, long? acquisitionRequestId, long? profileId, long? accountId)
        {
            OperationId = operationId;
            SourceLinkUrl = sourceLinkUrl;
            AcquisitionRequestId = acquisitionRequestId;
            ProfileId = profileId;
            AccountId = accountId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.SourceLinkUrl), true)]
        public string SourceLinkUrl { get; }

        // A user confirms only their own request; the Owner's Activity leaves these empty.
        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id), false)]
        public long? AcquisitionRequestId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id), false)]
        public long? ProfileId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id), false)]
        public long? AccountId { get; }
    }
}
