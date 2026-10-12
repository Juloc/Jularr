using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Tmdb
{
    internal enum TmdbMediaKind : byte
    {
        Movie = 1,
        Series = 2
    }

    // A v4 read access token or a v3 API key; both stay out of logs.
    internal sealed record TmdbConnection([property: JsonIgnore] string Credential, string Language);

    internal sealed record TmdbSearchResult(int Id, string Title, string? OriginalTitle, int? Year, string? Overview, string? PosterPath);

    internal sealed record TmdbSeason(int SeasonNumber, string? Name, DateOnly? AirDate, int EpisodeCount);

    internal sealed record TmdbEpisode(int SeasonNumber, int EpisodeNumber, string? Name, DateOnly? AirDate);

    internal sealed record TmdbWork(TmdbMediaKind Kind, int Id, string Title, string? OriginalTitle, int? Year, string? Overview, string? PosterPath, string? ImdbId, int? TvdbId,
        IReadOnlyList<TmdbSeason> Seasons);

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
