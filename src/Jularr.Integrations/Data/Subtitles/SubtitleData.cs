namespace Jularr.Integrations.Data.Subtitles
{
    // What is looked for: a movie, or one episode of a series, by its provider ids.
    internal sealed class SubtitleQuery(bool isEpisode, string? imdbId, string? tmdbId, int? season, int? episode, IReadOnlyList<string> languages)
    {
        public bool IsEpisode { get; } = isEpisode;
        public string? ImdbId { get; } = imdbId;
        public string? TmdbId { get; } = tmdbId;
        public int? Season { get; } = season;
        public int? Episode { get; } = episode;

        // Two-letter codes.
        public IReadOnlyList<string> Languages { get; } = languages;
    }

    // One subtitle a provider offers; the handle is what its download needs.
    internal sealed class SubtitleOffer(string handle, string language, string? releaseName, bool isHearingImpaired, bool isForeignOnly, long downloads)
    {
        public string Handle { get; } = handle;
        public string Language { get; } = language;
        public string? ReleaseName { get; } = releaseName;
        public bool IsHearingImpaired { get; } = isHearingImpaired;
        public bool IsForeignOnly { get; } = isForeignOnly;
        public long Downloads { get; } = downloads;
    }

    internal sealed class SubtitleAccess(string baseUrl, string apiKey, string? username, string? password)
    {
        public string BaseUrl { get; } = baseUrl;
        public string ApiKey { get; } = apiKey;
        public string? Username { get; } = username;
        public string? Password { get; } = password;
    }

    // Codes match the acquisition connection errors.
    internal sealed class SubtitleException(string code, Exception? innerException = null) : Exception("The subtitle provider did not answer as expected.", innerException)
    {
        public string Code { get; } = code;
    }
}
