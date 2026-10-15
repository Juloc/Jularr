using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Provider;
using Jularr.Backend.Data.Fields.Reading;

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
        public SearchDiscoverDataV1(string query, MediaType? mediaTypeId = null, int page = 1)
        {
            Query = query;
            MediaTypeId = mediaTypeId;
            Page = page;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query))]
        public string Query { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Page))]
        public int Page { get; }
    }

    // Books, Manga, Light Novels, music and games come from their own catalogs.
    public sealed class SearchDiscoverCatalogDataV1 : BaseData
    {
        public SearchDiscoverCatalogDataV1(MediaType mediaTypeId, string query, MediaSubtype? mediaSubtypeId = null)
        {
            MediaTypeId = mediaTypeId;
            Query = query;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query))]
        public string Query { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }
    }

    public sealed class DiscoverCatalogItemV1 : BaseResponse
    {
        public DiscoverCatalogItemV1(string @namespace, string externalId, string title, int? releaseYear, string? author, long? workId)
        {
            Namespace = @namespace;
            ExternalId = externalId;
            Title = title;
            ReleaseYear = releaseYear;
            Author = author;
            WorkId = workId;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string ExternalId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Author))]
        public string? Author { get; }

        // Set when the title already is a Work.
        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }
    }

    public sealed class RequestDiscoverCatalogDataV1 : BaseData
    {
        public RequestDiscoverCatalogDataV1(MediaType mediaTypeId, string @namespace, string externalId, long uiLocaleId, MediaSubtype? mediaSubtypeId = null)
        {
            MediaTypeId = mediaTypeId;
            Namespace = @namespace;
            ExternalId = externalId;
            UiLocaleId = uiLocaleId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string ExternalId { get; }

        [DataField(typeof(Fields.Localization.UiLocaleFields), nameof(Fields.Localization.UiLocaleFields.Id))]
        public long UiLocaleId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }
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
            MediaStateType mediaStateTypeId, string? plexUrl)
        {
            MediaTypeId = mediaTypeId;
            TmdbId = tmdbId;
            PlexUrl = plexUrl;
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

        // Opens the title on the connected Plex server.
        [DataField(typeof(PlexFields), nameof(PlexFields.Url))]
        public string? PlexUrl { get; }
    }

    public sealed class ReadContinueParametersV1 : BaseParameters
    {
    }

    public sealed class ContinueItemV1 : BaseResponse
    {
        public ContinueItemV1(long workId, long mediaAssetId, MediaType mediaTypeId, string title, string? posterUrl, WorkUnitType? workUnitTypeId, decimal? unitNumber,
            decimal? parentUnitNumber, long positionMs, long? durationMs, decimal? progression, MediaAssetType mediaAssetTypeId)
        {
            Progression = progression;
            MediaAssetTypeId = mediaAssetTypeId;
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

        // Read share of a book or comic.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Progression), false)]
        public decimal? Progression { get; }

        // Books continue in the reader.
        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.MediaAssetTypeId))]
        public MediaAssetType MediaAssetTypeId { get; }
    }
}
