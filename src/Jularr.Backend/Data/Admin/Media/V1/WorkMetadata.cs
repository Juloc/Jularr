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
        public SearchMetadataDataV1(MediaType mediaTypeId, string query, int? releaseYear = null, MediaSubtype? mediaSubtypeId = null)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Query = query;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query))]
        public string Query { get; }

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

    public sealed class MetadataSearchResultV1 : BaseResponse
    {
        public MetadataSearchResultV1(string @namespace, string externalId, string title, string? originalTitle, int? releaseYear, string? overview, long? workId, string? author = null)
        {
            Namespace = @namespace;
            ExternalId = externalId;
            Author = author;
            Title = title;
            OriginalTitle = originalTitle;
            ReleaseYear = releaseYear;
            Overview = overview;
            WorkId = workId;
        }

        // The provider: tmdb_movie, tmdb_tv, openlibrary, anilist, musicbrainz or igdb.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace))]
        public string Namespace { get; }

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
        public ImportMetadataWorkDataV1(MediaType mediaTypeId, string externalId, MediaSubtype? mediaSubtypeId = null, string? @namespace = null)
        {
            Namespace = @namespace;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            ExternalId = externalId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string ExternalId { get; }

        // Books name their provider; without one Open Library is meant.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Namespace), false)]
        public string? Namespace { get; }

        public override void Validate()
        {
            // TMDB, AniList and IGDB ids are numbers; books use Open Library ids, albums MusicBrainz ids.
            base.Validate();
            if (!MediaSubtypes.Fits(MediaTypeId, MediaSubtypeId))
            {
                throw new DataValidationException(nameof(MediaSubtypeId), "unknown_catalog_value");
            }

            if (MediaTypeId is MediaType.Movie or MediaType.Series && !int.TryParse(ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new DataValidationException(nameof(ExternalId), "invalid_type");
            }
        }
    }
}
