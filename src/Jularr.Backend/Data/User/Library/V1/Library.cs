using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Library.V1
{
    public sealed class ReadLibraryParametersV1 : BaseParameters
    {
        public ReadLibraryParametersV1(MediaType[]? mediaTypeIds = null, string? query = null, LibrarySortType librarySortTypeId = LibrarySortType.RecentlyAdded, int page = 1, int pageSize = 48,
            MediaSubtype[]? mediaSubtypeIds = null, int? releaseYearFrom = null, int? releaseYearTo = null)
        {
            MediaTypeIds = mediaTypeIds ?? [];
            MediaSubtypeIds = mediaSubtypeIds ?? [];
            ReleaseYearFrom = releaseYearFrom;
            ReleaseYearTo = releaseYearTo;
            Query = query;
            LibrarySortTypeId = librarySortTypeId;
            Page = page;
            PageSize = pageSize;
        }

        // A type lists its Works without subtype, a subtype lists that subtype across types; none lists everything.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeIds))]
        public MediaType[] MediaTypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeIds))]
        public MediaSubtype[] MediaSubtypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear), false)]
        public int? ReleaseYearFrom { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear), false)]
        public int? ReleaseYearTo { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query), false)]
        public string? Query { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LibrarySortTypeId))]
        public LibrarySortType LibrarySortTypeId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class LibraryItemV1 : BaseResponse
    {
        public LibraryItemV1(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string title, int? releaseYear, string? posterUrl, decimal? rating, MediaStateType mediaStateTypeId, bool isWatchlisted,
            MediaAvailabilityState availabilityStateId, int availableUnits, int monitoredUnits, string[] audioLanguages, string[] subtitleLanguages, ProgressStateType progressStateId, int progressPercent)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Title = title;
            ReleaseYear = releaseYear;
            PosterUrl = posterUrl;
            Rating = rating;
            MediaStateTypeId = mediaStateTypeId;
            IsWatchlisted = isWatchlisted;
            AvailabilityStateId = availabilityStateId;
            AvailableUnits = availableUnits;
            MonitoredUnits = monitoredUnits;
            AudioLanguages = audioLanguages;
            SubtitleLanguages = subtitleLanguages;
            ProgressStateId = progressStateId;
            ProgressPercent = progressPercent;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        // The catalog's rating on a scale of ten; null while no catalog entry of the title has one.
        [DataField(typeof(WorkFields), nameof(WorkFields.Rating))]
        public decimal? Rating { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.MediaStateTypeId))]
        public MediaStateType MediaStateTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }

        // What the title offers the profile: the library, its acquisition and the audio language.
        [DataField(typeof(WorkFields), nameof(WorkFields.AvailabilityStateId))]
        public MediaAvailabilityState AvailabilityStateId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.AvailableUnits))]
        public int AvailableUnits { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MonitoredUnits))]
        public int MonitoredUnits { get; }

        // The languages of the tracks that are really there.
        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Languages))]
        public string[] AudioLanguages { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Languages))]
        public string[] SubtitleLanguages { get; }

        // Where the profile stands with the title; the percentage is of its units or of its position.
        [DataField(typeof(WorkFields), nameof(WorkFields.ProgressStateId))]
        public ProgressStateType ProgressStateId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ProgressPercent))]
        public int ProgressPercent { get; }
    }
}
