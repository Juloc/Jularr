using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Discover.V1
{
    public sealed class DiscoverParametersV1 : BaseParameters
    {
    }

    public sealed class ReadDiscoverFeedDataV1 : BaseData
    {
        public ReadDiscoverFeedDataV1(MediaType mediaTypeId, DiscoverFeedType discoverFeedTypeId, int page = 1)
        {
            MediaTypeId = mediaTypeId;
            DiscoverFeedTypeId = discoverFeedTypeId;
            Page = page;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.DiscoverFeedTypeId))]
        public DiscoverFeedType DiscoverFeedTypeId { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Page))]
        public int Page { get; }
    }

    public sealed class SearchDiscoverDataV1 : BaseData
    {
        public SearchDiscoverDataV1(string query, MediaType? mediaTypeId = null)
        {
            Query = query;
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query))]
        public string Query { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }
    }

    public sealed class DiscoverItemParametersV1 : BaseParameters
    {
        public DiscoverItemParametersV1(MediaType mediaTypeId, int tmdbId)
        {
            MediaTypeId = mediaTypeId;
            TmdbId = tmdbId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TmdbId))]
        public int TmdbId { get; }
    }

    public sealed class DiscoverItemV1 : BaseResponse
    {
        public DiscoverItemV1(MediaType mediaTypeId, int? tmdbId, long? workId, string title, int? releaseYear, string? overview, string? posterUrl, string? backdropUrl,
            MediaStateType mediaStateTypeId)
        {
            MediaTypeId = mediaTypeId;
            TmdbId = tmdbId;
            WorkId = workId;
            Title = title;
            ReleaseYear = releaseYear;
            Overview = overview;
            PosterUrl = posterUrl;
            BackdropUrl = backdropUrl;
            MediaStateTypeId = mediaStateTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TmdbId), false)]
        public int? TmdbId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? BackdropUrl { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.MediaStateTypeId))]
        public MediaStateType MediaStateTypeId { get; }
    }

    public sealed class ReadContinueParametersV1 : BaseParameters
    {
    }

    public sealed class ContinueItemV1 : BaseResponse
    {
        public ContinueItemV1(long workId, long mediaAssetId, MediaType mediaTypeId, string title, string? posterUrl, WorkUnitType? workUnitTypeId, decimal? unitNumber,
            decimal? parentUnitNumber, long positionMs, long? durationMs)
        {
            WorkId = workId;
            MediaAssetId = mediaAssetId;
            MediaTypeId = mediaTypeId;
            Title = title;
            PosterUrl = posterUrl;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
            PositionMs = positionMs;
            DurationMs = durationMs;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.PositionMs))]
        public long PositionMs { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.DurationMs))]
        public long? DurationMs { get; }
    }
}
