using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class SearchMetadataParametersV1 : BaseParameters
    {
    }

    public sealed class SearchMetadataDataV1 : BaseData
    {
        public SearchMetadataDataV1(MediaType mediaTypeId, string query, int? releaseYear = null)
        {
            MediaTypeId = mediaTypeId;
            Query = query;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query))]
        public string Query { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        public override void Validate()
        {
            base.Validate();

            // TMDB covers movies and series.
            if (MediaTypeId is not (MediaType.Movie or MediaType.Series))
            {
                throw new DataValidationException(nameof(MediaTypeId), "unknown_catalog_value");
            }
        }
    }

    public sealed class MetadataSearchResultV1 : BaseResponse
    {
        public MetadataSearchResultV1(int tmdbId, string title, string? originalTitle, int? releaseYear, string? overview, long? workId)
        {
            TmdbId = tmdbId;
            Title = title;
            OriginalTitle = originalTitle;
            ReleaseYear = releaseYear;
            Overview = overview;
            WorkId = workId;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TmdbId))]
        public int TmdbId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.OriginalTitle))]
        public string? OriginalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }
    }

    public sealed class ImportMetadataWorkDataV1 : BaseData
    {
        public ImportMetadataWorkDataV1(MediaType mediaTypeId, int tmdbId)
        {
            MediaTypeId = mediaTypeId;
            TmdbId = tmdbId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TmdbId))]
        public int TmdbId { get; }

        public override void Validate()
        {
            base.Validate();
            if (MediaTypeId is not (MediaType.Movie or MediaType.Series))
            {
                throw new DataValidationException(nameof(MediaTypeId), "unknown_catalog_value");
            }
        }
    }
}
