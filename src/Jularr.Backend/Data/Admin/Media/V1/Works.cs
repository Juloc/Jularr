using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class CreateWorkParametersV1 : BaseParameters
    {
    }

    // The library list: every filter that is left out lets all titles through.
    public sealed class ReadWorksParametersV1 : BaseParameters
    {
        public ReadWorksParametersV1(int page = 1, int pageSize = 25, MediaType? mediaTypeId = null, bool? isMonitored = null, WorkListStatus? workListStatusId = null, long? libraryRootId = null,
            long? acquisitionProfileId = null, string? query = null, WorkSortType workSortTypeId = WorkSortType.Title, bool isDescending = false)
        {
            Page = page;
            PageSize = pageSize;
            MediaTypeId = mediaTypeId;
            IsMonitored = isMonitored;
            WorkListStatusId = workListStatusId;
            LibraryRootId = libraryRootId;
            AcquisitionProfileId = acquisitionProfileId;
            Query = query;
            WorkSortTypeId = workSortTypeId;
            IsDescending = isDescending;
        }

        public int Page { get; }
        public int PageSize { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        // Monitored titles have a target that is not paused; the others have none or a paused one.
        [DataField(typeof(WorkListFields), nameof(WorkListFields.IsMonitoredFilter))]
        public bool? IsMonitored { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkListStatusFilterId))]
        public WorkListStatus? WorkListStatusId { get; }

        // The library its target imports into, or one that holds a file of the title.
        [DataField(typeof(Fields.Library.LibraryRootFields), nameof(Fields.Library.LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(Fields.Acquisition.AcquisitionProfileFields), nameof(Fields.Acquisition.AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        // Part of the title; the header search leads here.
        [DataField(typeof(WorkListFields), nameof(WorkListFields.Query))]
        public string? Query { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkSortTypeId))]
        public WorkSortType WorkSortTypeId { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.IsDescending))]
        public bool IsDescending { get; }
    }

    // One row of the library list: the title with its cover, whether it is monitored, what it holds on disk and the profile and library its target uses.
    public sealed class WorkListItemV1 : BaseResponse
    {
        public WorkListItemV1(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear, DateTime createdAt, string? posterUrl, string? overview, long? wantedItemId,
            int targetCount, bool isMonitored, string? profileName, string? libraryName, int fileCount, long sizeBytes, WorkListStatus workListStatusId)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            CreatedAt = createdAt;
            PosterUrl = posterUrl;
            Overview = overview;
            WantedItemId = wantedItemId;
            TargetCount = targetCount;
            IsMonitored = isMonitored;
            ProfileName = profileName;
            LibraryName = libraryName;
            FileCount = fileCount;
            SizeBytes = sizeBytes;
            WorkListStatusId = workListStatusId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.ImageUrl))]
        public string? PosterUrl { get; }

        // The start of its synopsis, for the list's overview view.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Overview))]
        public string? Overview { get; }

        // Its first acquisition target; none until the title is wanted.
        [DataField(typeof(Fields.Acquisition.WantedItemFields), nameof(Fields.Acquisition.WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        // A game has one target per platform and is monitored there; every other title has at most one.
        [DataField(typeof(WorkListFields), nameof(WorkListFields.TargetCount))]
        public int TargetCount { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.IsMonitored))]
        public bool IsMonitored { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.ProfileName))]
        public string? ProfileName { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.LibraryName))]
        public string? LibraryName { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.FileCount))]
        public int FileCount { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.WorkListStatusId))]
        public WorkListStatus WorkListStatusId { get; }
    }

    // The numbers of the library list's filter chips and figures: every title that passes the type, library, profile and words of the list, by state.
    public sealed class WorksSummaryV1 : BaseResponse
    {
        public WorksSummaryV1(int totalCount, int availableCount, int missingCount, int downloadingCount, int unmonitoredCount, int monitoredCount)
        {
            TotalCount = totalCount;
            AvailableCount = availableCount;
            MissingCount = missingCount;
            DownloadingCount = downloadingCount;
            UnmonitoredCount = unmonitoredCount;
            MonitoredCount = monitoredCount;
        }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TitleCount))]
        public int TotalCount { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TitleCount))]
        public int AvailableCount { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TitleCount))]
        public int MissingCount { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TitleCount))]
        public int DownloadingCount { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TitleCount))]
        public int UnmonitoredCount { get; }

        [DataField(typeof(WorkListFields), nameof(WorkListFields.TitleCount))]
        public int MonitoredCount { get; }
    }

    public sealed class WorkParametersV1 : BaseParameters
    {
        public WorkParametersV1(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    public sealed class WorkDetailsV1 : BaseResponse
    {
        public WorkDetailsV1(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isMediaSubtypeManual, string canonicalTitle, int? releaseYear, long version,
            long? wantedItemId = null, string? franchiseName = null, decimal? franchisePosition = null, bool isFranchiseManual = false)
        {
            FranchiseName = franchiseName;
            FranchisePosition = franchisePosition;
            IsFranchiseManual = isFranchiseManual;
            WantedItemId = wantedItemId;
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsMediaSubtypeManual = isMediaSubtypeManual;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        // A manual subtype, also none, is kept by provider refreshes.
        [DataField(typeof(WorkFields), nameof(WorkFields.IsMediaSubtypeManual))]
        public bool IsMediaSubtypeManual { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }

        // Its acquisition target, once it is wanted.
        [DataField(typeof(Fields.Acquisition.WantedItemFields), nameof(Fields.Acquisition.WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        // Its series and place there; an administrator's choice is kept by imports.
        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name), false)]
        public string? FranchiseName { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Position), false)]
        public decimal? FranchisePosition { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsFranchiseManual))]
        public bool IsFranchiseManual { get; }
    }

    // A series by name with the Work's place in it; no name leaves the series to the providers again.
    public sealed class UpdateWorkFranchiseDataV1 : BaseData
    {
        public UpdateWorkFranchiseDataV1(string? franchiseName, decimal? franchisePosition)
        {
            FranchiseName = franchiseName;
            FranchisePosition = franchisePosition;
        }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name), false)]
        public string? FranchiseName { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Position), false)]
        public decimal? FranchisePosition { get; }
    }

    public sealed class CreateWorkDataV1 : BaseData
    {
        public CreateWorkDataV1(MediaType mediaTypeId, string canonicalTitle, int? releaseYear = null, MediaSubtype? mediaSubtypeId = null)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        public override void Validate()
        {
            base.Validate();
            if (!MediaSubtypes.Fits(MediaTypeId, MediaSubtypeId))
            {
                throw new DataValidationException(nameof(MediaSubtypeId), "unknown_catalog_value");
            }
        }
    }

    public sealed class UpdateWorkDataV1 : BaseData
    {
        public UpdateWorkDataV1(string canonicalTitle, long version, int? releaseYear = null, MediaSubtype? mediaSubtypeId = null)
        {
            CanonicalTitle = canonicalTitle;
            Version = version;
            ReleaseYear = releaseYear;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }
}
