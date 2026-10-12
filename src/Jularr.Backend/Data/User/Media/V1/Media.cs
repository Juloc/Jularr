using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Media.V1
{
    public sealed class ReadWatchlistParametersV1 : BaseParameters
    {
        public ReadWatchlistParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
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
        public WorkDetailsV1(long id, MediaType mediaTypeId, string canonicalTitle, bool isWatchlisted)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            IsWatchlisted = isWatchlisted;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }
    }

    public sealed class UpdateWatchlistDataV1 : BaseData
    {
        public UpdateWatchlistDataV1(bool isWatchlisted)
        {
            IsWatchlisted = isWatchlisted;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }
    }

    public sealed class ReadWorkAvailabilityParametersV1 : BaseParameters
    {
        public ReadWorkAvailabilityParametersV1(long workId, int page = 1, int pageSize = 25)
        {
            WorkId = workId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class WorkAvailabilityV1 : BaseResponse
    {
        public WorkAvailabilityV1(long id, long workVersionId, MediaAssetType mediaAssetTypeId, WorkUnitType? workUnitTypeId, decimal? unitNumber, decimal? parentUnitNumber,
            long fileCount, long sizeBytes, long? durationMs, bool isAvailable, int? height, string[] audioLanguages, string[] subtitleLanguages)
        {
            Id = id;
            WorkVersionId = workVersionId;
            MediaAssetTypeId = mediaAssetTypeId;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
            FileCount = fileCount;
            SizeBytes = sizeBytes;
            DurationMs = durationMs;
            IsAvailable = isAvailable;
            Height = height;
            AudioLanguages = audioLanguages;
            SubtitleLanguages = subtitleLanguages;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long Id { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkVersionId))]
        public long WorkVersionId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.MediaAssetTypeId))]
        public MediaAssetType MediaAssetTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.FileCount))]
        public long FileCount { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.IsAvailable))]
        public bool IsAvailable { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Height))]
        public int? Height { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Languages))]
        public string[] AudioLanguages { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Languages))]
        public string[] SubtitleLanguages { get; }
    }
}
