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

    // How AniList ranks a list; the newest only counts titles already out.
    internal enum AniListRanking : byte
    {
        Trending,
        Score,
        Newest
    }

    // A title as the provider catalog keeps it: one entry of a page that walks AniList by id.
    internal sealed class AniListCatalogEntry
    {
        public AniListCatalogEntry(int id, string type, string? format, string title, string? originalTitle, DateOnly? releasedOn, int? year, string? description, string? coverUrl,
            int? popularity, int? averageScore)
        {
            Id = id;
            Type = type;
            Format = format;
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
        public AniListListEntry(int mediaId, long listEntryId, bool isManga, string title, string status, int progress, int progressVolumes, int score, int? totalCount, int? totalVolumes,
            DateTimeOffset updatedAt)
        {
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
