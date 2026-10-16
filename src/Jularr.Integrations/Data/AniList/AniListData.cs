using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Data.AniList
{
    internal enum AniListFailure : byte
    {
        Unavailable,
        NotFound,
        RateLimited,
        InvalidResponse
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

        // The language of its country of origin: Japanese, Korean or Chinese.
        public string? OriginalLanguage { get; init; }
    }
}
