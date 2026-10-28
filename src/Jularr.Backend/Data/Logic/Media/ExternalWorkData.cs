using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Media
{
    // One provider title as Discover shows it; read back from the catalog it carries its Work and, for an anime season, the season of that series.
    internal sealed class ExternalWorkData : BaseData
    {
        public ExternalWorkData(string @namespace, string value, string title, string? originalTitle, int? releaseYear, string? overview, string? creator, string? imageUrl,
            string? backdropUrl, DateTime? fetchedAt, decimal? rating = null, long? workId = null, int? seasonNumber = null)
        {
            Rating = rating;
            Namespace = @namespace;
            Value = value;
            Title = title;
            OriginalTitle = originalTitle;
            ReleaseYear = releaseYear;
            Overview = overview;
            Creator = creator;
            ImageUrl = imageUrl;
            BackdropUrl = backdropUrl;
            FetchedAt = fetchedAt;
            WorkId = workId;
            SeasonNumber = seasonNumber;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value))]
        public string Value { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Title))]
        public string Title { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.OriginalTitle))]
        public string? OriginalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Creator))]
        public string? Creator { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ImageUrl))]
        public string? ImageUrl { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.BackdropUrl))]
        public string? BackdropUrl { get; }

        // When the catalog's own entry of the title was read; none while only lists named it.
        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.FetchedAt), false)]
        public DateTime? FetchedAt { get; }

        // On a scale of ten; null while the catalog has none.
        [DataField(typeof(WorkFields), nameof(WorkFields.Rating))]
        public decimal? Rating { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber), false)]
        public int? SeasonNumber { get; }

        // TMDB's and IMDb's ids of a TheTVDB series a TMDB list or Sonarr named; the Work keeps them as further identities.
        public string? TmdbValue { get; init; }
        public string? ImdbValue { get; init; }
    }

    // Whether a Work the profile may open has its details yet; a series that is only an anime season names its AniList entry.
    internal sealed class WorkDetailStateData : BaseData
    {
        public WorkDetailStateData(long id, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, bool isDetailed, string? aniListValue)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            IsDetailed = isDetailed;
            AniListValue = aniListValue;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.IsDetailed))]
        public bool IsDetailed { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value), false)]
        public string? AniListValue { get; }
    }

    // A Work whose details are looked up: for a profile only while it may see the Work, for a background pass (no profile) always.
    internal sealed class WorkDetailLookupData : BaseData
    {
        public WorkDetailLookupData(long id, long? profileId)
        {
            Id = id;
            ProfileId = profileId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id), false)]
        public long? ProfileId { get; }
    }

    // Anime seasons that were Works of their own and now go into the series that maps them.
    internal sealed class AnimePlaceholderMergeData : BaseData
    {
        public AnimePlaceholderMergeData(long seriesWorkId, long[] placeholderWorkIds)
        {
            SeriesWorkId = seriesWorkId;
            PlaceholderWorkIds = placeholderWorkIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long SeriesWorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Ids))]
        public long[] PlaceholderWorkIds { get; }
    }

    internal sealed class WorkIdsData : BaseData
    {
        public WorkIdsData(long[] workIds)
        {
            WorkIds = workIds;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Ids))]
        public long[] WorkIds { get; }
    }

    // A Work a title or folder may name, and whether something ties it to the library.
    internal sealed class WorkLibraryCandidateData : BaseData
    {
        public WorkLibraryCandidateData(long id, bool isInLibrary)
        {
            Id = id;
            IsInLibrary = isInLibrary;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.IsInLibrary))]
        public bool IsInLibrary { get; }
    }

    // A Work that shares a provider id with another: whether it is in the library and whether it holds the provider's own id.
    internal sealed class WorkTwinData : BaseData
    {
        public WorkTwinData(long id, bool isInLibrary, bool holdsValue)
        {
            Id = id;
            IsInLibrary = isInLibrary;
            HoldsValue = holdsValue;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.IsInLibrary))]
        public bool IsInLibrary { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.HoldsValue))]
        public bool HoldsValue { get; }
    }

    // The Work a saved catalog title is; an anime season is its series with the season.
    internal sealed class ExternalWorkSavedData : BaseData
    {
        public ExternalWorkSavedData(string value, long workId, int? seasonNumber)
        {
            Value = value;
            WorkId = workId;
            SeasonNumber = seasonNumber;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value))]
        public string Value { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber), false)]
        public int? SeasonNumber { get; }
    }

    // A catalog's list of one type: Namespace, type and subtype name it, trending ones are ranked in their order.
    internal sealed class ExternalWorkListData : BaseData
    {
        public ExternalWorkListData(string @namespace, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId)
        {
            Namespace = @namespace;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }
    }

    internal sealed class ExternalWorkBatchData : BaseData
    {
        public ExternalWorkBatchData(ExternalWorkListData list, IReadOnlyList<ExternalWorkData> works, bool isTrending)
        {
            Namespace = list.Namespace;
            MediaTypeId = (short)list.MediaTypeId;
            MediaSubtypeId = list.MediaSubtypeId is { } subtype ? (short)subtype : null;
            IsTrending = isTrending;
            Values = works.Select(work => work.Value).ToArray();
            Titles = works.Select(work => work.Title).ToArray();
            OriginalTitles = works.Select(work => work.OriginalTitle).ToArray();
            ReleaseYears = works.Select(work => work.ReleaseYear ?? 0).ToArray();
            Overviews = works.Select(work => work.Overview).ToArray();
            Creators = works.Select(work => work.Creator).ToArray();
            ImageUrls = works.Select(work => work.ImageUrl).ToArray();
            BackdropUrls = works.Select(work => work.BackdropUrl).ToArray();
            Ratings = works.Select(work => work.Rating ?? 0).ToArray();
            TmdbValues = works.Select(work => work.TmdbValue).ToArray();
            ImdbValues = works.Select(work => work.ImdbValue).ToArray();
        }

        public string Namespace { get; }
        public short MediaTypeId { get; }
        public short? MediaSubtypeId { get; }
        public bool IsTrending { get; }
        public string[] Values { get; }
        public string[] Titles { get; }
        public string?[] OriginalTitles { get; }
        // Arrays carry no NULL numbers; 0 stands for an unknown year.
        public int[] ReleaseYears { get; }
        public string?[] Overviews { get; }
        public string?[] Creators { get; }
        public string?[] ImageUrls { get; }
        public string?[] BackdropUrls { get; }
        public decimal[] Ratings { get; }
        public string?[] TmdbValues { get; }
        public string?[] ImdbValues { get; }
    }

    // One Discover shelf of a whole catalog, or a scope to search without a list: Shelf numbers it within the request.
    internal sealed class ExternalWorkShelfData
    {
        public ExternalWorkShelfData(int shelf, string @namespace, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, DiscoverFeedType? feed = null)
        {
            Shelf = shelf;
            Namespace = @namespace;
            MediaTypeId = (short)mediaTypeId;
            // JSON rows carry no NULL numbers here; 0 stands for no subtype.
            MediaSubtypeId = mediaSubtypeId is { } subtype ? (short)subtype : (short)0;
            Feed = (short)(feed ?? 0);
        }

        public int Shelf { get; }
        public string Namespace { get; }
        public short MediaTypeId { get; }
        public short MediaSubtypeId { get; }
        public short Feed { get; }
    }

    // Shelves read in one statement; a browse page goes on at its offset.
    internal sealed class ExternalWorkShelfQueryData : BaseData
    {
        public ExternalWorkShelfQueryData(IReadOnlyList<ExternalWorkShelfData> shelves, long profileId, DateOnly today, int offset = 0)
        {
            Shelves = JsonSerializer.Serialize(shelves);
            ProfileId = profileId;
            Today = today;
            Offset = offset;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Shelves))]
        public string Shelves { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Day))]
        public DateOnly Today { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Offset))]
        public int Offset { get; }
    }

    // How a whole catalog ranks its lists; set by ExternalWorkLogic.
    internal sealed class ExternalWorkRankingData : BaseData
    {
        public ExternalWorkRankingData(int limit, int topVotes, DateOnly newSince, DateOnly upcomingUntil)
        {
            Limit = limit;
            TopVotes = topVotes;
            NewSince = newSince;
            UpcomingUntil = upcomingUntil;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.TopVotes))]
        public int TopVotes { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Day))]
        public DateOnly NewSince { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Day))]
        public DateOnly UpcomingUntil { get; }
    }

    // A catalog title on a shelf or among the suggestions, with the profile's Work when it already is one.
    internal sealed class ExternalWorkShelfItemData : BaseData
    {
        public ExternalWorkShelfItemData(int shelf, string @namespace, string value, string title, int? releaseYear, string? overview, string? creator, string? imageUrl,
            string? backdropUrl, decimal? rating, long workId, MediaStateType? mediaStateTypeId, string? plexUrl)
        {
            Rating = rating;
            Shelf = shelf;
            Namespace = @namespace;
            Value = value;
            Title = title;
            ReleaseYear = releaseYear;
            Overview = overview;
            Creator = creator;
            ImageUrl = imageUrl;
            BackdropUrl = backdropUrl;
            WorkId = workId;
            MediaStateTypeId = mediaStateTypeId;
            PlexUrl = plexUrl;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Shelf))]
        public int Shelf { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value))]
        public string Value { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Title))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Overview))]
        public string? Overview { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Creator))]
        public string? Creator { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ImageUrl))]
        public string? ImageUrl { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.BackdropUrl))]
        public string? BackdropUrl { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Rating))]
        public decimal? Rating { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        // The profile's state of the Work; none while the profile may not see it.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.MediaStateTypeId), false)]
        public MediaStateType? MediaStateTypeId { get; }

        [DataField(typeof(PlexFields), nameof(PlexFields.Url))]
        public string? PlexUrl { get; }
    }

    // A typed search over the scopes of kept catalogs: Shelf numbers each scope.
    internal sealed class ExternalWorkSuggestionQueryData : BaseData
    {
        public ExternalWorkSuggestionQueryData(IReadOnlyList<ExternalWorkShelfData> scopes, long profileId, string searchTerms, string titlePrefix, int limit)
        {
            Shelves = JsonSerializer.Serialize(scopes);
            ProfileId = profileId;
            SearchTerms = searchTerms;
            TitlePrefix = titlePrefix;
            Limit = limit;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Shelves))]
        public string Shelves { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.SearchTerms))]
        public string SearchTerms { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.SearchTerms))]
        public string TitlePrefix { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }
    }

    // A catalog's full-sync settings and progress.
    internal sealed class ExternalWorkSyncData : BaseData
    {
        public ExternalWorkSyncData(string @namespace, int requestsPerMinute, bool pausesWhileUsed, DateOnly? listedOn, int? cursor, DateTime? changesReadAt)
        {
            Namespace = @namespace;
            RequestsPerMinute = requestsPerMinute;
            PausesWhileUsed = pausesWhileUsed;
            ListedOn = listedOn;
            Cursor = cursor;
            ChangesReadAt = changesReadAt;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.RequestsPerMinute))]
        public int RequestsPerMinute { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ListedOn))]
        public DateOnly? ListedOn { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Cursor))]
        public int? Cursor { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ChangesReadAt))]
        public DateTime? ChangesReadAt { get; }
    }

    // A catalog whose Discover lists get their details in advance: its requests a pass and whether it waits while Jularr is used.
    internal sealed class CatalogPrefetchData : BaseData
    {
        public CatalogPrefetchData(string @namespace, int requestsPerMinute, bool pausesWhileUsed)
        {
            Namespace = @namespace;
            RequestsPerMinute = requestsPerMinute;
            PausesWhileUsed = pausesWhileUsed;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.RequestsPerMinute))]
        public int RequestsPerMinute { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }
    }

    // The next titles of a catalog's Discover lists without details, leaving out those a pass just could not detail.
    internal sealed class CatalogPrefetchQueryData : BaseData
    {
        public CatalogPrefetchQueryData(string @namespace, int limit, long[] skipped)
        {
            Namespace = @namespace;
            Limit = limit;
            Skipped = skipped;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.WorkIds))]
        public long[] Skipped { get; }
    }

    internal sealed class ExternalWorkSyncProgressData : BaseData
    {
        public ExternalWorkSyncProgressData(string @namespace, DateOnly? listedOn, int? cursor, DateTime? changesReadAt)
        {
            Namespace = @namespace;
            ListedOn = listedOn;
            Cursor = cursor;
            ChangesReadAt = changesReadAt;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ListedOn))]
        public DateOnly? ListedOn { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Cursor))]
        public int? Cursor { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ChangesReadAt))]
        public DateTime? ChangesReadAt { get; }
    }

    internal sealed class ExternalWorkSyncWindowData : BaseData
    {
        public ExternalWorkSyncWindowData(string @namespace, TimeOnly startsAt, TimeOnly endsAt)
        {
            Namespace = @namespace;
            StartsAt = startsAt;
            EndsAt = endsAt;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.StartsAt))]
        public TimeOnly StartsAt { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.EndsAt))]
        public TimeOnly EndsAt { get; }
    }

    internal sealed class ExternalWorkSyncSummaryData : BaseData
    {
        public ExternalWorkSyncSummaryData(string @namespace, int requestsPerMinute, bool pausesWhileUsed, DateOnly? listedOn, long entries, long pending, long version)
        {
            Namespace = @namespace;
            RequestsPerMinute = requestsPerMinute;
            PausesWhileUsed = pausesWhileUsed;
            ListedOn = listedOn;
            Entries = entries;
            Pending = pending;
            Version = version;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.RequestsPerMinute))]
        public int RequestsPerMinute { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.ListedOn))]
        public DateOnly? ListedOn { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Entries))]
        public long Entries { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Pending))]
        public long Pending { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Version))]
        public long Version { get; }
    }

    // The Owner's choices for one catalog, with its windows as JSON rows.
    internal sealed class ExternalWorkSyncSettingsData : BaseData
    {
        public ExternalWorkSyncSettingsData(string @namespace, int requestsPerMinute, bool pausesWhileUsed, long version, string windows)
        {
            Namespace = @namespace;
            RequestsPerMinute = requestsPerMinute;
            PausesWhileUsed = pausesWhileUsed;
            Version = version;
            Windows = windows;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.RequestsPerMinute))]
        public int RequestsPerMinute { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.PausesWhileUsed))]
        public bool PausesWhileUsed { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Version))]
        public long Version { get; }

        public string Windows { get; }
    }

    // The next entries a full sync reads, most popular first.
    internal sealed class ExternalWorkQueueData : BaseData
    {
        public ExternalWorkQueueData(string @namespace, int limit)
        {
            Namespace = @namespace;
            Limit = limit;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Limit))]
        public int Limit { get; }
    }

    // Many titles of one catalog list as JSON rows (a provider's id list, or its entries just read) and the ones it no longer has.
    internal sealed class ExternalWorkEntriesData : BaseData
    {
        public ExternalWorkEntriesData(ExternalWorkListData list, string entries, string[]? gone = null)
        {
            Namespace = list.Namespace;
            MediaTypeId = (short)list.MediaTypeId;
            MediaSubtypeId = list.MediaSubtypeId is { } subtype ? (short)subtype : null;
            Entries = entries;
            Gone = gone ?? [];
        }

        public string Namespace { get; }
        public short MediaTypeId { get; }
        public short? MediaSubtypeId { get; }
        public string Entries { get; }
        public string[] Gone { get; }
    }

    internal sealed class ExternalWorkNamespaceData : BaseData
    {
        public ExternalWorkNamespaceData(string @namespace)
        {
            Namespace = @namespace;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Namespace))]
        public string Namespace { get; }
    }

    internal sealed class ExternalWorkValueData : BaseData
    {
        public ExternalWorkValueData(string value)
        {
            Value = value;
        }

        [DataField(typeof(ExternalWorkFields), nameof(ExternalWorkFields.Value))]
        public string Value { get; }
    }

    // Values of one catalog, such as the entries a provider changed.
    internal sealed class ExternalWorkValuesData : BaseData
    {
        public ExternalWorkValuesData(string @namespace, string[] values)
        {
            Namespace = @namespace;
            Values = values;
        }

        public string Namespace { get; }
        public string[] Values { get; }
    }
}
