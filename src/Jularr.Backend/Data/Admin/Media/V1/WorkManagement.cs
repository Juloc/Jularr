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
        public WorkManagementUnitV1(long id, long? parentWorkUnitId, WorkUnitType workUnitTypeId, decimal number, string? title, DateOnly? releasedOn, bool isMonitored, WorkUnitStatus workUnitStatusId,
            int? absoluteNumber = null, decimal? aniListNumber = null, int? discNumber = null)
        {
            DiscNumber = discNumber;
            AbsoluteNumber = absoluteNumber;
            AniListNumber = aniListNumber;
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

        // An episode's place among all episodes of the title, specials left out; anime releases count this way.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.AbsoluteNumber))]
        public int? AbsoluteNumber { get; }

        // The disc of an album's track.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.DiscNumber), false)]
        public int? DiscNumber { get; }

        // The number the episode has in its AniList entry, when its season is mapped to one.
        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? AniListNumber { get; }
    }

    // One audio track of a file, to give it its own language.
    public sealed class WorkManagementTrackV1 : BaseResponse
    {
        public WorkManagementTrackV1(long storedFileId, int streamIndex, string? codec, int? channels, string? language, string? title)
        {
            StoredFileId = storedFileId;
            StreamIndex = streamIndex;
            Codec = codec;
            Channels = channels;
            Language = language;
            Title = title;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.StreamIndex))]
        public int StreamIndex { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Codec), false)]
        public string? Codec { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Channels), false)]
        public int? Channels { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Language), false)]
        public string? Language { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Title), false)]
        public string? Title { get; }
    }

    // One video file of the title: what it is, how good it is and where it lies.
    public sealed class WorkManagementFileV1 : BaseResponse
    {
        public WorkManagementFileV1(long id, long[] workUnitIds, string relativePath, long sizeBytes, string? qualityKey, FileQualityGrade fileQualityGradeId, int? formatScore, FileSizeGrade fileSizeGradeId,
            decimal? megabytesPerMinute, string? videoCodec, int? height, string? dynamicRange, string[] audioLanguages, string[] subtitleLanguages, string? releaseGroup, long? durationMs, bool isMissing,
            long? groupId = null, bool? isRomVerified = null, string? gameRegion = null, string? gameVersion = null, BaseDataList<WorkManagementTrackV1>? audioTracks = null, int? pageCount = null)
        {
            PageCount = pageCount;
            AudioTracks = audioTracks ?? new BaseDataList<WorkManagementTrackV1>([]);
            GameRegion = gameRegion;
            GameVersion = gameVersion;
            GroupId = groupId;
            IsRomVerified = isRomVerified;
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

        // The edition or platform it belongs to.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.GroupId), false)]
        public long? GroupId { get; }

        public BaseDataList<WorkManagementTrackV1> AudioTracks { get; }

        // The pages of a comic archive, once counted.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.PageCount), false)]
        public int? PageCount { get; }

        // What a game file's name says: the region and the version of the dump.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.GameRegion), false)]
        public string? GameRegion { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.GameVersion), false)]
        public string? GameVersion { get; }

        // A game file whose hash a ROM catalog of its platform names; none when no hash is known.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.IsRomVerified), false)]
        public bool? IsRomVerified { get; }
    }

    // An edition of a book or a platform of a game with what the page shows of it; a platform carries the target that is monitored and searched for it.
    public sealed class WorkManagementGroupV1 : BaseResponse
    {
        public WorkManagementGroupV1(long id, WorkGroupKind workGroupKindId, string? name, string? title, string? languageCode, EditionFormat? editionFormatId, string? publisher, int? releaseYear, string? isbn,
            long? wantedItemId, bool? isPaused)
        {
            Id = id;
            WorkGroupKindId = workGroupKindId;
            Name = name;
            Title = title;
            LanguageCode = languageCode;
            EditionFormatId = editionFormatId;
            Publisher = publisher;
            ReleaseYear = releaseYear;
            Isbn = isbn;
            WantedItemId = wantedItemId;
            IsPaused = isPaused;
        }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.GroupId))]
        public long Id { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.WorkGroupKindId))]
        public WorkGroupKind WorkGroupKindId { get; }

        // The edition's name or the platform's.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.GroupName), false)]
        public string? Name { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.GroupName), false)]
        public string? Title { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.LanguageCode), false)]
        public string? LanguageCode { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.EditionFormatId), false)]
        public EditionFormat? EditionFormatId { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Publisher), false)]
        public string? Publisher { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear), false)]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Isbn), false)]
        public string? Isbn { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.IsPaused), false)]
        public bool? IsPaused { get; }
    }

    // Everything the management page of one title shows.
    public sealed class WorkManagementV1 : BaseResponse
    {
        public WorkManagementV1(long id, long? wantedItemId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear, int? runtimeMinutes, DateOnly? releasedOn,
            string? overview, string? genres, string? studio, string? posterUrl, string? backdropUrl, long onDiskBytes, long missingCount, long upgradeWantedCount, long unairedCount, long seasonCount,
            long episodeCount, BaseDataList<WorkManagementUnitV1> units, BaseDataList<WorkManagementFileV1> files, BaseDataList<WorkManagementGroupV1>? groups = null)
        {
            Groups = groups ?? new BaseDataList<WorkManagementGroupV1>([]);
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

        public BaseDataList<WorkManagementGroupV1> Groups { get; }
    }
}
