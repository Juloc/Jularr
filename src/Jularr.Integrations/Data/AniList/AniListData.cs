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

    internal sealed class AniListManga
    {
        public AniListManga(int id, string romajiTitle, string? englishTitle, string? nativeTitle, IReadOnlyList<string> synonyms, int? year, int? volumes, string? status,
            string? description, string? coverUrl, IReadOnlyList<string> authors)
        {
            Id = id;
            RomajiTitle = romajiTitle;
            EnglishTitle = englishTitle;
            NativeTitle = nativeTitle;
            Synonyms = synonyms;
            Year = year;
            Volumes = volumes;
            Status = status;
            Description = description;
            CoverUrl = coverUrl;
            Authors = authors;
        }

        public int Id { get; }
        public string RomajiTitle { get; }
        public string? EnglishTitle { get; }
        public string? NativeTitle { get; }
        public IReadOnlyList<string> Synonyms { get; }
        public int? Year { get; }

        // Released volumes; unknown while a series runs.
        public int? Volumes { get; }
        public string? Status { get; }
        public string? Description { get; }
        public string? CoverUrl { get; }
        public IReadOnlyList<string> Authors { get; }
    }
}
