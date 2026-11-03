using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Data.AniList
{
    internal enum AniListFailure : byte
    {
        Unavailable,
        NotFound,
        RateLimited,
        InvalidResponse,
        Unauthorized
    }

    internal sealed class AniListException : Exception
    {
        public AniListException(AniListFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public AniListFailure Failure { get; }
    }

    // AniList's orders of a list, as its search offers them.
    internal enum AniListSort : byte
    {
        Trending,
        Popularity,
        Score,
        Favourites,
        Added,
        StartDate,
        Title
    }

    // One name of the filter's lists: a tag (with its category) or a site.
    internal sealed record AniListOption(int Id, string Name, string? Category, bool IsAdult);

    internal sealed record AniListFilterOptions(IReadOnlyList<AniListOption> Tags, IReadOnlyList<AniListOption> AnimeSites, IReadOnlyList<AniListOption> MangaSites);

    // A count from and up to, both included; either end may be open.
    internal readonly record struct AniListRange(int? Minimum, int? Maximum);

    // One ranked list: its order and what narrows it; a season needs its year.
    internal sealed class AniListListQuery
    {
        public AniListListQuery(AniListSort sort, string? season = null, int? seasonYear = null, string? country = null)
        {
            Sort = sort;
            Season = season;
            SeasonYear = seasonYear;
            Country = country;
        }

        public AniListSort Sort { get; }
        // WINTER, SPRING, SUMMER or FALL.
        public string? Season { get; }
        public int? SeasonYear { get; }
        // Two letters, KR for Korea.
        public string? Country { get; }

        // What Discover's filters narrow it by; an empty choice narrows nothing. Genres and tags must all match, the others any one.
        public IReadOnlyList<string> Genres { get; init; } = [];
        public IReadOnlyList<string> Tags { get; init; } = [];
        public IReadOnlyList<string> Statuses { get; init; } = [];
        public IReadOnlyList<string> Sources { get; init; } = [];
        public IReadOnlyList<int> SiteIds { get; init; } = [];
        // Years the title began in, both included.
        public int? YearFrom { get; init; }
        public int? YearTo { get; init; }
        public AniListRange Episodes { get; init; }
        public AniListRange Chapters { get; init; }
        public AniListRange Volumes { get; init; }
        // Minutes of one episode.
        public AniListRange Duration { get; init; }
        public bool IncludesAdult { get; init; }
    }

    // A title as the provider catalog keeps it: one entry of a page that walks AniList by id.
    internal sealed class AniListCatalogEntry
    {
        public AniListCatalogEntry(int id, string type, string? format, string title, string? originalTitle, DateOnly? releasedOn, int? year, string? description, string? coverUrl,
            int? popularity, int? averageScore, int? favourites, string? country, string? season, int? seasonYear, AniListFacts facts)
        {
            Favourites = favourites;
            Country = country;
            Season = season;
            SeasonYear = seasonYear;
            Id = id;
            Type = type;
            Format = format;
            Facts = facts;
            Title = title;
            OriginalTitle = originalTitle;
            ReleasedOn = releasedOn;
            Year = year;
            Description = description;
            CoverUrl = coverUrl;
            Popularity = popularity;
            AverageScore = averageScore;
        }

        public int Id { get; }
        // ANIME or MANGA.
        public string Type { get; }
        public string? Format { get; }
        public string Title { get; }
        public string? OriginalTitle { get; }
        public DateOnly? ReleasedOn { get; }
        public int? Year { get; }
        public string? Description { get; }
        public string? CoverUrl { get; }
        // How many AniList users list it.
        public int? Popularity { get; }
        // On a scale of a hundred.
        public int? AverageScore { get; }
        // How many AniList users made it a favourite.
        public int? Favourites { get; }
        // Two letters, KR for Korea.
        public string? Country { get; }
        // The anime season it began in, with its year.
        public string? Season { get; }
        public int? SeasonYear { get; }
        public AniListFacts Facts { get; }
    }

    // What else AniList's search filters by, in AniList's own values.
    internal sealed class AniListFacts
    {
        public AniListFacts(string? status, string? source, int? episodes, int? chapters, int? volumes, int? duration, IReadOnlyList<string> genres, IReadOnlyList<int> tagIds,
            IReadOnlyList<int> siteIds, bool isAdult)
        {
            Status = status;
            Source = source;
            Episodes = episodes;
            Chapters = chapters;
            Volumes = volumes;
            Duration = duration;
            Genres = genres;
            TagIds = tagIds;
            SiteIds = siteIds;
            IsAdult = isAdult;
        }

        // FINISHED, RELEASING, NOT_YET_RELEASED, CANCELLED or HIATUS.
        public string? Status { get; }
        public string? Source { get; }
        public int? Episodes { get; }
        public int? Chapters { get; }
        public int? Volumes { get; }
        // Minutes of one episode.
        public int? Duration { get; }
        public IReadOnlyList<string> Genres { get; }
        public IReadOnlyList<int> TagIds { get; }
        // The streaming or reading sites of its official links.
        public IReadOnlyList<int> SiteIds { get; }
        public bool IsAdult { get; }
    }

    internal enum AniListMediaType : byte
    {
        Anime,
        Manga
    }

    internal sealed class AniListMedia
    {
        public AniListMedia(int id, string? format, string romajiTitle, string? englishTitle, string? nativeTitle, IReadOnlyList<string> synonyms, int? year,
            int? episodes, int? volumes, string? description, string? coverUrl, IReadOnlyList<string> authors, IReadOnlyList<int> sequels, IReadOnlyList<int> prequels,
            string? bannerUrl, string? trailerKey)
        {
            Prequels = prequels;
            BannerUrl = bannerUrl;
            TrailerKey = trailerKey;
            Id = id;
            Format = format;
            RomajiTitle = romajiTitle;
            EnglishTitle = englishTitle;
            NativeTitle = nativeTitle;
            Synonyms = synonyms;
            Year = year;
            Episodes = episodes;
            Volumes = volumes;
            Description = description;
            CoverUrl = coverUrl;
            Authors = authors;
            Sequels = sequels;
        }

        public int Id { get; }
        public string? Format { get; }
        public string RomajiTitle { get; }
        public string? EnglishTitle { get; }
        public string? NativeTitle { get; }
        public IReadOnlyList<string> Synonyms { get; }
        public int? Year { get; }

        // Episodes and released volumes; unknown while a series runs.
        public int? Episodes { get; }
        public int? Volumes { get; }
        public string? Description { get; }
        public string? CoverUrl { get; }
        public IReadOnlyList<string> Authors { get; }

        public IReadOnlyList<int> Sequels { get; }
        public IReadOnlyList<int> Prequels { get; }

        // The wide artwork and the YouTube id of this entry's trailer.
        public string? BannerUrl { get; }
        public string? TrailerKey { get; }

        // The credited people with their ids at this provider.
        public IReadOnlyList<ProviderPerson> People { get; init; } = [];

        // The story staff in their own script, as Japanese sites credit them.
        public IReadOnlyList<string> NativeAuthors { get; init; } = [];

        // The language of its country of origin: Japanese, Korean or Chinese.
        public string? OriginalLanguage { get; init; }

        // Every title AniList relates it to, with the kind of relation.
        public IReadOnlyList<AniListRelation> Relations { get; init; } = [];
    }

    // What AniList hands out for an authorization code of a person's own account.
    internal sealed class AniListToken
    {
        public AniListToken(string accessToken, DateTimeOffset? expiresAt)
        {
            AccessToken = accessToken;
            ExpiresAt = expiresAt;
        }

        public string AccessToken { get; }
        public DateTimeOffset? ExpiresAt { get; }
    }

    internal sealed class AniListViewer
    {
        public AniListViewer(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }
        public string Name { get; }
    }

    // One entry of a person's list; chapters and volumes of a title are read as its totals.
    internal sealed class AniListListEntry
    {
        public AniListListEntry(int mediaId, long listEntryId, bool isManga, string title, string? coverUrl, bool isNovel, int? releaseYear, string status, int progress, int progressVolumes, int score,
            int? totalCount, int? totalVolumes, DateTimeOffset updatedAt)
        {
            CoverUrl = coverUrl;
            IsNovel = isNovel;
            ReleaseYear = releaseYear;
            MediaId = mediaId;
            ListEntryId = listEntryId;
            IsManga = isManga;
            Title = title;
            Status = status;
            Progress = progress;
            ProgressVolumes = progressVolumes;
            Score = score;
            TotalCount = totalCount;
            TotalVolumes = totalVolumes;
            UpdatedAt = updatedAt;
        }

        public int MediaId { get; }
        public long ListEntryId { get; }
        public bool IsManga { get; }
        public string Title { get; }
        public string? CoverUrl { get; }

        // A book of AniList's manga list that is a (light) novel.
        public bool IsNovel { get; }
        public int? ReleaseYear { get; }
        public string Status { get; }

        // Episodes or chapters, and volumes.
        public int Progress { get; }
        public int ProgressVolumes { get; }

        // Points out of a hundred; 0 is unscored.
        public int Score { get; }

        // Episodes (anime) or chapters (manga), and volumes, when AniList knows them.
        public int? TotalCount { get; }
        public int? TotalVolumes { get; }
        public DateTimeOffset UpdatedAt { get; }
    }

    // The part of an entry a progress write answers with.
    internal sealed class AniListSavedEntry
    {
        public AniListSavedEntry(int progress, int progressVolumes, DateTimeOffset updatedAt)
        {
            Progress = progress;
            ProgressVolumes = progressVolumes;
            UpdatedAt = updatedAt;
        }

        public int Progress { get; }
        public int ProgressVolumes { get; }
        public DateTimeOffset UpdatedAt { get; }
    }

    // A title related to an entry: AniList's relation type (SEQUEL, ADAPTATION, SIDE_STORY ...) and what the related entry is.
    internal sealed record AniListRelation(string RelationType, int Id, string Type, string? Format, string Title, int? Year, string? CoverUrl);
}
