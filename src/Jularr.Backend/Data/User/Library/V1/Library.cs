using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Library.V1
{
    public sealed class ReadLibraryParametersV1 : BaseParameters
    {
        public ReadLibraryParametersV1(MediaType? mediaTypeId = null, string? query = null, LibrarySortType librarySortTypeId = LibrarySortType.RecentlyAdded, int page = 1, int pageSize = 48,
            MediaSubtype? mediaSubtypeId = null)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Query = query;
            LibrarySortTypeId = librarySortTypeId;
            Page = page;
            PageSize = pageSize;
        }

        // A type alone lists its Works without subtype; a subtype lists that subtype across types.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query), false)]
        public string? Query { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LibrarySortTypeId))]
        public LibrarySortType LibrarySortTypeId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class LibraryItemV1 : BaseResponse
    {
        public LibraryItemV1(long id, MediaType mediaTypeId, string title, int? releaseYear, string? posterUrl, string? jxlPosterUrl, MediaStateType mediaStateTypeId, bool isWatchlisted)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            Title = title;
            ReleaseYear = releaseYear;
            PosterUrl = posterUrl;
            JxlPosterUrl = jxlPosterUrl;
            MediaStateTypeId = mediaStateTypeId;
            IsWatchlisted = isWatchlisted;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl), false)]
        public string? JxlPosterUrl { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.MediaStateTypeId))]
        public MediaStateType MediaStateTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }
    }
}
