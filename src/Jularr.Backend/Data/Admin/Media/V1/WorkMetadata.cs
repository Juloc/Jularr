using System.Globalization;
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

            // TMDB covers movies and series, Open Library books, AniList Manga, MusicBrainz albums, IGDB games.
            if (MediaTypeId is not (MediaType.Movie or MediaType.Series or MediaType.Book or MediaType.Manga or MediaType.LightNovel or MediaType.Music or MediaType.Game))
            {
                throw new DataValidationException(nameof(MediaTypeId), "unknown_catalog_value");
            }
        }
    }

    public sealed class MetadataSearchResultV1 : BaseResponse
    {
        public MetadataSearchResultV1(string externalId, string title, string? originalTitle, int? releaseYear, string? overview, long? workId, string? author = null)
        {
            ExternalId = externalId;
            Author = author;
            Title = title;
            OriginalTitle = originalTitle;
            ReleaseYear = releaseYear;
            Overview = overview;
            WorkId = workId;
        }

        // TMDB id for video, Open Library work id for books.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string ExternalId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Author))]
        public string? Author { get; }

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
        public ImportMetadataWorkDataV1(MediaType mediaTypeId, string externalId)
        {
            MediaTypeId = mediaTypeId;
            ExternalId = externalId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string ExternalId { get; }

        public override void Validate()
        {
            // TMDB, AniList and IGDB ids are numbers; books use Open Library ids, albums MusicBrainz ids.
            base.Validate();
            if (MediaTypeId is not (MediaType.Movie or MediaType.Series or MediaType.Book or MediaType.Manga or MediaType.LightNovel or MediaType.Music or MediaType.Game))
            {
                throw new DataValidationException(nameof(MediaTypeId), "unknown_catalog_value");
            }

            if (MediaTypeId is MediaType.Movie or MediaType.Series && !int.TryParse(ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new DataValidationException(nameof(ExternalId), "invalid_type");
            }
        }
    }
}
