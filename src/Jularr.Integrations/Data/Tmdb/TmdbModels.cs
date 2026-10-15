using System.Text.Json.Serialization;

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
        public TmdbSearchResult(int id, string title, string? originalTitle, int? year, string? overview, string? posterPath, string? backdropPath)
        {
            BackdropPath = backdropPath;
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
    }

    internal sealed class TmdbSeason
    {
        public TmdbSeason(int seasonNumber, string? name, DateOnly? airDate, int episodeCount, string? posterPath = null)
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

    internal sealed class TmdbEpisode
    {
        public TmdbEpisode(int seasonNumber, int episodeNumber, string? name, DateOnly? airDate, string? overview = null, string? stillPath = null, int? runtime = null)
        {
            SeasonNumber = seasonNumber;
            EpisodeNumber = episodeNumber;
            Name = name;
            AirDate = airDate;
            Overview = overview;
            StillPath = stillPath;
            Runtime = runtime;
        }

        public int SeasonNumber { get; }
        public int EpisodeNumber { get; }
        public string? Name { get; }
        public DateOnly? AirDate { get; }
        public string? Overview { get; }
        public string? StillPath { get; }

        // Minutes.
        public int? Runtime { get; }
    }

    internal sealed class TmdbWork
    {
        public TmdbWork(TmdbMediaKind kind, int id, string title, string? originalTitle, int? year, string? overview, string? posterPath, string? backdropPath, string? imdbId, int? tvdbId,
            IReadOnlyList<TmdbSeason> seasons, IReadOnlyList<int> genreIds, string? originalLanguage, IReadOnlyList<string> originCountries, int? collectionId,
            string? collectionName, DateOnly? releasedOn, TmdbDetails? details = null)
        {
            Details = details ?? new TmdbDetails([], null, [], null, [], []);
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
    }

    // What a details page describes beyond the identity, named in the requested language.
    internal sealed class TmdbDetails
    {
        public TmdbDetails(IReadOnlyList<string> genres, int? runtime, IReadOnlyList<string> studios, string? trailerKey, IReadOnlyList<TmdbCredit> cast, IReadOnlyList<TmdbCredit> crew)
        {
            Genres = genres;
            Runtime = runtime;
            Studios = studios;
            TrailerKey = trailerKey;
            Cast = cast;
            Crew = crew;
        }

        public IReadOnlyList<string> Genres { get; }
        public int? Runtime { get; }
        public IReadOnlyList<string> Studios { get; }

        // A YouTube video id.
        public string? TrailerKey { get; }
        public IReadOnlyList<TmdbCredit> Cast { get; }
        public IReadOnlyList<TmdbCredit> Crew { get; }
    }

    // A person with their character or job.
    internal sealed class TmdbCredit
    {
        public TmdbCredit(string name, string? role, string? profilePath)
        {
            Name = name;
            Role = role;
            ProfilePath = profilePath;
        }

        public string Name { get; }
        public string? Role { get; }
        public string? ProfilePath { get; }
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
}
