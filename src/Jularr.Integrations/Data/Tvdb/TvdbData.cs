using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Tvdb
{
    internal sealed class TvdbSession(string token)
    {
        [JsonIgnore]
        public string Token { get; } = token;
    }

    internal sealed class TvdbSeries(int id, string name, string? imdbId)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public string? ImdbId { get; } = imdbId;
    }

    internal sealed class TvdbEpisode(int id, int seasonNumber, int episodeNumber, int? absoluteNumber, string? name, DateOnly? aired, int? runtime)
    {
        public int Id { get; } = id;
        public int SeasonNumber { get; } = seasonNumber;
        public int EpisodeNumber { get; } = episodeNumber;
        public int? AbsoluteNumber { get; } = absoluteNumber;
        public string? Name { get; } = name;
        public DateOnly? Aired { get; } = aired;
        public int? Runtime { get; } = runtime;

        public TvdbEpisode WithName(string? other) => new(Id, SeasonNumber, EpisodeNumber, AbsoluteNumber, other, Aired, Runtime);
    }

    internal enum TvdbFailure : byte
    {
        Unavailable = 1,
        AuthenticationFailed = 2,
        RateLimited = 3,
        NotFound = 4,
        InvalidResponse = 5,
        Ambiguous = 6
    }

    internal sealed class TvdbException(TvdbFailure failure, string message, Exception? innerException = null) : Exception(message, innerException)
    {
        public TvdbFailure Failure { get; } = failure;
    }
}
