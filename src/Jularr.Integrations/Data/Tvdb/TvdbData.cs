using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Tvdb
{
    internal sealed class TvdbSession(string token)
    {
        [JsonIgnore]
        public string Token { get; } = token;
    }

    // A series as TheTVDB describes it; languages are TheTVDB's three-letter codes, images absolute addresses.
    internal sealed class TvdbSeries(int id, string name, string? imdbId)
    {
        public int Id { get; } = id;
        // In the series' original language.
        public string Name { get; } = name;
        public string? ImdbId { get; } = imdbId;
        public int? TmdbId { get; init; }
        public string? Overview { get; init; }
        public string? ImageUrl { get; init; }
        public string? OriginalLanguage { get; init; }
        public string? OriginalCountry { get; init; }
        public DateOnly? FirstAired { get; init; }
        public int? Year { get; init; }
        public string? Status { get; init; }
        public IReadOnlyList<TvdbTranslation> Translations { get; init; } = [];
        public IReadOnlyList<TvdbTranslation> Aliases { get; init; } = [];
        public IReadOnlyList<TvdbArtwork> Artworks { get; init; } = [];
        public IReadOnlyList<string> Genres { get; init; } = [];
        // The original network first, then the other networks and studios.
        public IReadOnlyList<string> Networks { get; init; } = [];
        public IReadOnlyList<TvdbTrailer> Trailers { get; init; } = [];
        public IReadOnlyList<TvdbCharacter> Characters { get; init; } = [];
        // The seasons of the aired order with their images.
        public IReadOnlyList<TvdbSeason> Seasons { get; init; } = [];
    }

    internal sealed class TvdbTranslation(string language, string? name, string? overview)
    {
        public string Language { get; } = language;
        public string? Name { get; } = name;
        public string? Overview { get; } = overview;
    }

    internal sealed class TvdbArtwork(TvdbArtworkType type, string imageUrl, string? language, double score)
    {
        public TvdbArtworkType Type { get; } = type;
        public string ImageUrl { get; } = imageUrl;
        // None for an image without text.
        public string? Language { get; } = language;
        public double Score { get; } = score;
    }

    // TheTVDB's artwork types of a series.
    internal enum TvdbArtworkType : byte
    {
        Banner = 1,
        Poster = 2,
        Background = 3,
        Icon = 5,
        ClearArt = 22,
        ClearLogo = 23
    }

    // A trailer TheTVDB links on YouTube.
    internal sealed class TvdbTrailer(string youTubeKey, string? language)
    {
        public string YouTubeKey { get; } = youTubeKey;
        public string? Language { get; } = language;
    }

    // A person credited for the series: an actor with the character played, or a director, writer, creator or producer.
    internal sealed class TvdbCharacter(int? personId, string personName, string role, string? character, string? imageUrl, int sort)
    {
        public int? PersonId { get; } = personId;
        public string PersonName { get; } = personName;
        // TheTVDB's people type, such as Actor, Guest Star, Director, Writer or Creator.
        public string Role { get; } = role;
        public string? Character { get; } = character;
        public string? ImageUrl { get; } = imageUrl;
        public int Sort { get; } = sort;
    }

    internal sealed class TvdbSeason(int number, string? imageUrl)
    {
        public int Number { get; } = number;
        public string? ImageUrl { get; } = imageUrl;
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
        public string? Overview { get; init; }
        public string? ImageUrl { get; init; }
    }

    // One series of TheTVDB's search with its names and summaries by TheTVDB's language code.
    internal sealed class TvdbSearchResult(int id, string name)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public int? Year { get; init; }
        public string? Overview { get; init; }
        public string? ImageUrl { get; init; }
        public string? OriginalLanguage { get; init; }
        public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> Overviews { get; init; } = new Dictionary<string, string>();
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
