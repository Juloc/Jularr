using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.User.Media.V1
{
    public sealed class ReadWatchlistParametersV1 : BaseParameters
    {
        public ReadWatchlistParametersV1(int page = 1, int pageSize = 25, MediaType? mediaTypeId = null, MediaSubtype? mediaSubtypeId = null,
            LibrarySortType librarySortTypeId = LibrarySortType.RecentlyAdded)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            LibrarySortTypeId = librarySortTypeId;
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }

        // A type alone lists its Works without subtype; a subtype lists that subtype across types.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }

        // Recently added means added to the watchlist.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LibrarySortTypeId))]
        public LibrarySortType LibrarySortTypeId { get; }
    }

    public sealed class ReadCreatorWorksParametersV1 : BaseParameters
    {
        public ReadCreatorWorksParametersV1(string name, int page = 1, int pageSize = 25)
        {
            Name = name;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Creator))]
        public string Name { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class WorkParametersV1 : BaseParameters
    {
        public WorkParametersV1(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    // A platform a game can be requested for.
    public sealed class GamePlatformV1 : BaseResponse
    {
        public GamePlatformV1(long id, string name, string? abbreviation)
        {
            Id = id;
            Name = name;
            Abbreviation = abbreviation;
        }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id))]
        public long Id { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Name))]
        public string Name { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Abbreviation))]
        public string? Abbreviation { get; }
    }

    // What a poster card in a list shows.
    public sealed class WorkCardV1 : BaseResponse
    {
        public WorkCardV1(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, bool isWatchlisted, int? releaseYear, string? posterUrl)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            IsWatchlisted = isWatchlisted;
            ReleaseYear = releaseYear;
            PosterUrl = posterUrl;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }
    }

    public sealed class WorkDetailsV1 : BaseResponse
    {
        public WorkDetailsV1(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, bool isWatchlisted, int? releaseYear, string? overview, string? posterUrl, string? backdropUrl,
            string? plexUrl, string[] platforms, string[] creators, long? franchiseId, string? franchiseName, bool isRequestable, AcquisitionRequestStatusType? requestStatusTypeId)
        {
            RequestStatusTypeId = requestStatusTypeId;
            IsRequestable = isRequestable;
            FranchiseId = franchiseId;
            FranchiseName = franchiseName;
            Creators = creators;
            Platforms = platforms;
            PlexUrl = plexUrl;
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            IsWatchlisted = isWatchlisted;
            ReleaseYear = releaseYear;
            Overview = overview;
            PosterUrl = posterUrl;
            BackdropUrl = backdropUrl;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        // The subtype as the instance shows it; none while that subtype is switched off.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? BackdropUrl { get; }

        // Opens the title on the connected Plex server.
        [DataField(typeof(PlexFields), nameof(PlexFields.Url))]
        public string? PlexUrl { get; }

        // Platforms a game is available on.
        [DataField(typeof(WorkFields), nameof(WorkFields.Titles))]
        public string[] Platforms { get; }

        // The authors or artists, each on its own.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Creators))]
        public string[] Creators { get; }

        // The provider's film series, while franchises are switched on.
        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Id), false)]
        public long? FranchiseId { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name), false)]
        public string? FranchiseName { get; }

        // A Work with media and without episodes or volumes to come, or with an open request of the profile, needs no request.
        [DataField(typeof(WorkFields), nameof(WorkFields.IsRequestable))]
        public bool IsRequestable { get; }

        // The profile's latest request for the Work.
        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId), false)]
        public AcquisitionRequestStatusType? RequestStatusTypeId { get; }
    }

    public sealed class UpdateWatchlistDataV1 : BaseData
    {
        public UpdateWatchlistDataV1(bool isWatchlisted)
        {
            IsWatchlisted = isWatchlisted;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }
    }

    public sealed class ReadWorkAvailabilityParametersV1 : BaseParameters
    {
        public ReadWorkAvailabilityParametersV1(long workId, int page = 1, int pageSize = 25)
        {
            WorkId = workId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class WorkAvailabilityV1 : BaseResponse
    {
        public WorkAvailabilityV1(long id, long workVersionId, MediaAssetType mediaAssetTypeId, WorkUnitType? workUnitTypeId, decimal? unitNumber, decimal? parentUnitNumber,
            long fileCount, long sizeBytes, long? durationMs, bool isAvailable, int? height, string[] audioLanguages, string[] subtitleLanguages, long? resumePositionMs, bool isCompleted, bool isEmulated,
            string? groupName, string? unitTitle)
        {
            UnitTitle = unitTitle;
            GroupName = groupName;
            IsEmulated = isEmulated;
            Id = id;
            WorkVersionId = workVersionId;
            MediaAssetTypeId = mediaAssetTypeId;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
            FileCount = fileCount;
            SizeBytes = sizeBytes;
            DurationMs = durationMs;
            IsAvailable = isAvailable;
            Height = height;
            AudioLanguages = audioLanguages;
            SubtitleLanguages = subtitleLanguages;
            ResumePositionMs = resumePositionMs;
            IsCompleted = isCompleted;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long Id { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkVersionId))]
        public long WorkVersionId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.MediaAssetTypeId))]
        public MediaAssetType MediaAssetTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.FileCount))]
        public long FileCount { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.IsAvailable))]
        public bool IsAvailable { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Height))]
        public int? Height { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Languages))]
        public string[] AudioLanguages { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Languages))]
        public string[] SubtitleLanguages { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? ResumePositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted))]
        public bool IsCompleted { get; }

        // A game of a platform the browser plays.
        [DataField(typeof(GameFields), nameof(GameFields.IsEmulated))]
        public bool IsEmulated { get; }

        // The named section the unit falls into, if any.
        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Name), false)]
        public string? GroupName { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitTitle))]
        public string? UnitTitle { get; }
    }

    public sealed class ReadWorkEpisodesParametersV1 : BaseParameters
    {
        public ReadWorkEpisodesParametersV1(long workId, int seasonNumber)
        {
            WorkId = workId;
            SeasonNumber = seasonNumber;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int SeasonNumber { get; }
    }

    // A season with its poster, year and how much of it is here.
    public sealed class WorkSeasonV1 : BaseResponse
    {
        public WorkSeasonV1(int seasonNumber, string? title, string? imageUrl, int? releaseYear, int episodeCount, int availableCount)
        {
            SeasonNumber = seasonNumber;
            Title = title;
            ImageUrl = imageUrl;
            ReleaseYear = releaseYear;
            EpisodeCount = episodeCount;
            AvailableCount = availableCount;
        }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int SeasonNumber { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Title))]
        public string? Title { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.ImageUrl))]
        public string? ImageUrl { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.EpisodeCount))]
        public int EpisodeCount { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.AvailableCount))]
        public int AvailableCount { get; }
    }

    // An episode as a card: still, summary and length, what plays and where this profile stopped, and its state.
    public sealed class WorkEpisodeV1 : BaseResponse
    {
        public WorkEpisodeV1(long id, int seasonNumber, decimal number, string? title, string? overview, string? imageUrl, int? runtimeMinutes, DateOnly? releasedOn,
            long? mediaAssetId, long? resumePositionMs, long? durationMs, bool isCompleted, MediaStateType mediaStateTypeId, bool isUpcoming, string? groupName)
        {
            GroupName = groupName;
            Id = id;
            SeasonNumber = seasonNumber;
            Number = number;
            Title = title;
            Overview = overview;
            ImageUrl = imageUrl;
            RuntimeMinutes = runtimeMinutes;
            ReleasedOn = releasedOn;
            MediaAssetId = mediaAssetId;
            ResumePositionMs = resumePositionMs;
            DurationMs = durationMs;
            IsCompleted = isCompleted;
            MediaStateTypeId = mediaStateTypeId;
            IsUpcoming = isUpcoming;
        }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int SeasonNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber))]
        public decimal Number { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Title))]
        public string? Title { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.ImageUrl))]
        public string? ImageUrl { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.RuntimeMinutes))]
        public int? RuntimeMinutes { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.ReleasedOn))]
        public DateOnly? ReleasedOn { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id), false)]
        public long? MediaAssetId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? ResumePositionMs { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs), false)]
        public long? DurationMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted))]
        public bool IsCompleted { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.MediaStateTypeId))]
        public MediaStateType MediaStateTypeId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.IsUpcoming))]
        public bool IsUpcoming { get; }

        // The staff's section the episode falls in.
        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Name), false)]
        public string? GroupName { get; }
    }
}
