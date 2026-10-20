using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    // A season, an episode or a volume of the title with where it stands.
    public sealed class WorkManagementUnitV1 : BaseResponse
    {
        public WorkManagementUnitV1(long id, long? parentWorkUnitId, WorkUnitType workUnitTypeId, decimal number, string? title, DateOnly? releasedOn, bool isMonitored, WorkUnitStatus workUnitStatusId)
        {
            Id = id;
            ParentWorkUnitId = parentWorkUnitId;
            WorkUnitTypeId = workUnitTypeId;
            Number = number;
            Title = title;
            ReleasedOn = releasedOn;
            IsMonitored = isMonitored;
            WorkUnitStatusId = workUnitStatusId;
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

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.WorkUnitStatusId))]
        public WorkUnitStatus WorkUnitStatusId { get; }
    }

    // One video file of the title: what it is, how good it is and where it lies.
    public sealed class WorkManagementFileV1 : BaseResponse
    {
        public WorkManagementFileV1(long id, long[] workUnitIds, string relativePath, long sizeBytes, string? qualityKey, FileQualityGrade fileQualityGradeId, int? formatScore, FileSizeGrade fileSizeGradeId,
            decimal? megabytesPerMinute, string? videoCodec, int? height, string? dynamicRange, string[] audioLanguages, string[] subtitleLanguages, string? releaseGroup, long? durationMs, bool isMissing)
        {
            Id = id;
            WorkUnitIds = workUnitIds;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            QualityKey = qualityKey;
            FileQualityGradeId = fileQualityGradeId;
            FormatScore = formatScore;
            FileSizeGradeId = fileSizeGradeId;
            MegabytesPerMinute = megabytesPerMinute;
            VideoCodec = videoCodec;
            Height = height;
            DynamicRange = dynamicRange;
            AudioLanguages = audioLanguages;
            SubtitleLanguages = subtitleLanguages;
            ReleaseGroup = releaseGroup;
            DurationMs = durationMs;
            IsMissing = isMissing;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        // The units it covers: its own and the further ones of a file that holds several episodes; none for a film.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.UnitIds))]
        public long[] WorkUnitIds { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.FileQualityGradeId))]
        public FileQualityGrade FileQualityGradeId { get; }

        // The custom-format score of its release name under the profile of the title.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Score), false)]
        public int? FormatScore { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.FileSizeGradeId))]
        public FileSizeGrade FileSizeGradeId { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.MegabytesPerMinute), false)]
        public decimal? MegabytesPerMinute { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Codec), false)]
        public string? VideoCodec { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Height), false)]
        public int? Height { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.DynamicRange), false)]
        public string? DynamicRange { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Languages))]
        public string[] AudioLanguages { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Languages))]
        public string[] SubtitleLanguages { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ReleaseGroup), false)]
        public string? ReleaseGroup { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs), false)]
        public long? DurationMs { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.IsMissing))]
        public bool IsMissing { get; }
    }

    // Everything the management page of one title shows.
    public sealed class WorkManagementV1 : BaseResponse
    {
        public WorkManagementV1(long id, long? wantedItemId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear, int? runtimeMinutes, DateOnly? releasedOn,
            string? overview, string? genres, string? studio, string? posterUrl, string? backdropUrl, long onDiskBytes, long missingCount, long upgradeWantedCount, long unairedCount, long seasonCount,
            long episodeCount, BaseDataList<WorkManagementUnitV1> units, BaseDataList<WorkManagementFileV1> files)
        {
            Id = id;
            WantedItemId = wantedItemId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            RuntimeMinutes = runtimeMinutes;
            ReleasedOn = releasedOn;
            Overview = overview;
            Genres = genres;
            Studio = studio;
            PosterUrl = posterUrl;
            BackdropUrl = backdropUrl;
            OnDiskBytes = onDiskBytes;
            MissingCount = missingCount;
            UpgradeWantedCount = upgradeWantedCount;
            UnairedCount = unairedCount;
            SeasonCount = seasonCount;
            EpisodeCount = episodeCount;
            Units = units;
            Files = files;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.RuntimeMinutes))]
        public int? RuntimeMinutes { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleasedOn))]
        public DateOnly? ReleasedOn { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Genres))]
        public string? Genres { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Studio))]
        public string? Studio { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.ImageUrl))]
        public string? PosterUrl { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.ImageUrl))]
        public string? BackdropUrl { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long OnDiskBytes { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.UnitCount))]
        public long MissingCount { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.UnitCount))]
        public long UpgradeWantedCount { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.UnitCount))]
        public long UnairedCount { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.UnitCount))]
        public long SeasonCount { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.UnitCount))]
        public long EpisodeCount { get; }

        public BaseDataList<WorkManagementUnitV1> Units { get; }

        public BaseDataList<WorkManagementFileV1> Files { get; }
    }
}
