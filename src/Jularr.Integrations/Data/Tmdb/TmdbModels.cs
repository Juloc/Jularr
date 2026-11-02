using System.Text.Json.Serialization;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Data.Tmdb
{
    internal enum TmdbMediaKind : byte
    {
        Movie = 1,
        Series = 2
    }

    // A v4 read access token or a v3 API key; both stay out of logs.
    internal sealed class TmdbConnection
    {
        public TmdbConnection(string credential, string language)
        {
            Credential = credential;
            Language = language;
        }

        [JsonIgnore]
        public string Credential { get; }
        public string Language { get; }
    }

    internal sealed class TmdbSearchResult
    {
        public TmdbSearchResult(int id, string title, string? originalTitle, int? year, string? overview, string? posterPath, string? backdropPath, double? voteAverage = null)
        {
            BackdropPath = backdropPath;
            VoteAverage = voteAverage;
            Id = id;
            Title = title;
            OriginalTitle = originalTitle;
            Year = year;
            Overview = overview;
            PosterPath = posterPath;
        }

        public int Id { get; }
        public string Title { get; }
        public string? OriginalTitle { get; }
        public int? Year { get; }
        public string? Overview { get; }
        public string? PosterPath { get; }
        public string? BackdropPath { get; }
        // TMDB's average vote on a scale of ten; absent while nobody voted.
        public double? VoteAverage { get; }
    }

    internal sealed class TmdbSeason
    {
        public TmdbSeason(int seasonNumber, string? name, DateOnly? airDate, int episodeCount, string? posterPath)
        {
            SeasonNumber = seasonNumber;
            Name = name;
            AirDate = airDate;
            EpisodeCount = episodeCount;
            PosterPath = posterPath;
        }

        public int SeasonNumber { get; }
        public string? Name { get; }
        public DateOnly? AirDate { get; }
        public int EpisodeCount { get; }
        public string? PosterPath { get; }
    }

    internal sealed class TmdbWork
    {
        public TmdbWork(TmdbMediaKind kind, int id, string title, string? originalTitle, int? year, string? overview, string? posterPath, string? backdropPath, string? imdbId, int? tvdbId,
            IReadOnlyList<TmdbSeason> seasons, IReadOnlyList<int> genreIds, string? originalLanguage, IReadOnlyList<string> originCountries, int? collectionId,
            string? collectionName, DateOnly? releasedOn, TmdbDetails details)
        {
            Details = details;
            ReleasedOn = releasedOn;
            CollectionId = collectionId;
            CollectionName = collectionName;
            GenreIds = genreIds;
            OriginalLanguage = originalLanguage;
            OriginCountries = originCountries;
            BackdropPath = backdropPath;
            Kind = kind;
            Id = id;
            Title = title;
            OriginalTitle = originalTitle;
            Year = year;
            Overview = overview;
            PosterPath = posterPath;
            ImdbId = imdbId;
            TvdbId = tvdbId;
            Seasons = seasons;
        }

        public TmdbMediaKind Kind { get; }
        public int Id { get; }
        public string Title { get; }
        public string? OriginalTitle { get; }
        public int? Year { get; }
        public DateOnly? ReleasedOn { get; }
        public string? Overview { get; }
        public string? PosterPath { get; }
        public string? BackdropPath { get; }
        public string? ImdbId { get; }
        public int? TvdbId { get; }
        public IReadOnlyList<TmdbSeason> Seasons { get; }
        public IReadOnlyList<int> GenreIds { get; }
        public string? OriginalLanguage { get; }
        public IReadOnlyList<string> OriginCountries { get; }

        // A movie's film series.
        public int? CollectionId { get; }
        public string? CollectionName { get; }

        public TmdbDetails Details { get; }

        // Its title and summary in other languages.
        public IReadOnlyList<TmdbTranslation> Translations { get; init; } = [];
    }

    // What a details page describes beyond the identity, named in the requested language.
    internal sealed class TmdbDetails
    {
        public TmdbDetails(IReadOnlyList<string> genres, int? runtime, IReadOnlyList<string> studios, string? trailerKey, IReadOnlyList<ProviderPerson> people)
        {
            Genres = genres;
            Runtime = runtime;
            Studios = studios;
            TrailerKey = trailerKey;
            People = people;
        }

        public IReadOnlyList<string> Genres { get; }
        public int? Runtime { get; }
        public IReadOnlyList<string> Studios { get; }

        // A YouTube video id.
        public string? TrailerKey { get; }
        // Cast in billing order, then the key crew.
        public IReadOnlyList<ProviderPerson> People { get; }
    }

    // Ranked lists; Upcoming exists for movies only.
    internal enum TmdbFeed : byte
    {
        Trending = 1,
        Top = 2,
        New = 3,
        Upcoming = 4
    }

    internal enum TmdbFailure : byte
    {
        Unavailable = 1,
        AuthenticationFailed = 2,
        RateLimited = 3,
        NotFound = 4,
        InvalidResponse = 5
    }

    // Messages never contain the credential.
    internal sealed class TmdbException(TmdbFailure failure, string message, Exception? innerException = null) : Exception(message, innerException)
    {
        public TmdbFailure Failure { get; } = failure;
    }

    // One line of TMDB's daily id list: the id, the original title and how popular the title is.
    internal sealed class TmdbExportEntry
    {
        public TmdbExportEntry(int id, string originalTitle, double popularity, bool isAdult, bool isVideo)
        {
            Id = id;
            OriginalTitle = originalTitle;
            Popularity = popularity;
            IsAdult = isAdult;
            IsVideo = isVideo;
        }

        public int Id { get; }
        public string OriginalTitle { get; }
        public double Popularity { get; }
        public bool IsAdult { get; }
        // A video extra rather than a film.
        public bool IsVideo { get; }
    }

    // A title as the provider catalog keeps it, in the connection's language.
    internal sealed class TmdbCatalogEntry
    {
        public TmdbCatalogEntry(int id, string title, string? originalTitle, DateOnly? releasedOn, string? overview, string? posterPath, string? backdropPath, double? popularity,
            double? voteAverage, int? voteCount, string? imdbId, bool isAdult)
        {
            Id = id;
            Title = title;
            OriginalTitle = originalTitle;
            ReleasedOn = releasedOn;
            Overview = overview;
            PosterPath = posterPath;
            BackdropPath = backdropPath;
            Popularity = popularity;
            VoteAverage = voteAverage;
            VoteCount = voteCount;
            ImdbId = imdbId;
            IsAdult = isAdult;
        }

        public int Id { get; }
        public string Title { get; }
        public string? OriginalTitle { get; }
        public DateOnly? ReleasedOn { get; }
        public string? Overview { get; }
        public string? PosterPath { get; }
        public string? BackdropPath { get; }
        public double? Popularity { get; }
        // TMDB's average vote on a scale of ten.
        public double? VoteAverage { get; }
        public int? VoteCount { get; }
        public string? ImdbId { get; }
        public bool IsAdult { get; }
        // A series' link to TheTVDB.
        public int? TvdbId { get; init; }
    }

    // A language (ISO 639-1), its region (ISO 3166-1) and the texts TMDB has in it.
    internal sealed record TmdbTranslation(string Language, string? Region, string? Title, string? Overview);
}
