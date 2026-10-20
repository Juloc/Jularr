using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    // The facts of a title that its management page shows at the top.
    internal sealed class WorkManagementHeroData : BaseData
    {
        public WorkManagementHeroData(long id, long? wantedItemId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear, int? runtimeMinutes, DateOnly? releasedOn,
            string? overview, string? genres, string? studio, string? posterUrl, string? backdropUrl)
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
    }

    // A video file of the title as stored, with what its probe and its release name say.
    internal sealed class WorkManagementFileData : BaseData
    {
        public WorkManagementFileData(long id, long mediaAssetId, long? workUnitId, string relativePath, long sizeBytes, string? qualityKey, string? releaseGroup, long? durationMs, bool isMissing,
            string? videoCodec, int? height, string? dynamicRange, string[] audioLanguages, string[] subtitleLanguages, string? evidence)
        {
            Id = id;
            MediaAssetId = mediaAssetId;
            WorkUnitId = workUnitId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            QualityKey = qualityKey;
            ReleaseGroup = releaseGroup;
            DurationMs = durationMs;
            IsMissing = isMissing;
            VideoCodec = videoCodec;
            Height = height;
            DynamicRange = dynamicRange;
            AudioLanguages = audioLanguages;
            SubtitleLanguages = subtitleLanguages;
            Evidence = evidence;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId), false)]
        public long? WorkUnitId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ReleaseGroup))]
        public string? ReleaseGroup { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs), false)]
        public long? DurationMs { get; }

        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.IsMissing))]
        public bool IsMissing { get; }

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

        // What the format matching reads from the release name, as JSON.
        [DataField(typeof(WorkManagementFields), nameof(WorkManagementFields.Overview))]
        public string? Evidence { get; }
    }

    // A further unit of the same work that a stored asset covers.
    internal sealed class WorkManagementAssetUnitData : BaseData
    {
        public WorkManagementAssetUnitData(long mediaAssetId, long workUnitId)
        {
            MediaAssetId = mediaAssetId;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId))]
        public long WorkUnitId { get; }
    }
}
