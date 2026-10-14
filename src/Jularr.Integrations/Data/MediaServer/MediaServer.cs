using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.MediaServer
{
    // Jellyfin grew out of Emby; both speak the same API, Jellyfin with the user in the query.
    internal enum MediaServerDialect : byte
    {
        Jellyfin,
        Emby
    }

    internal sealed class MediaServerConnection
    {
        public MediaServerConnection(Uri baseUrl, string apiKey, MediaServerDialect dialect)
        {
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            Dialect = dialect;
        }

        public Uri BaseUrl { get; }

        [JsonIgnore]
        public string ApiKey { get; }

        public MediaServerDialect Dialect { get; }
    }

    internal enum MediaServerFailure : byte
    {
        Unavailable,
        Unauthorized,
        InvalidResponse
    }

    internal sealed class MediaServerException : Exception
    {
        public MediaServerException(MediaServerFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public MediaServerFailure Failure { get; }
    }

    internal sealed class MediaServerInfo
    {
        public MediaServerInfo(string id, string name, string? version)
        {
            Id = id;
            Name = name;
            Version = version;
        }

        public string Id { get; }
        public string Name { get; }
        public string? Version { get; }
    }

    internal sealed class MediaServerUser
    {
        public MediaServerUser(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
    }

    internal enum MediaServerItemKind : byte
    {
        Movie,
        Series,
        Episode
    }

    // One movie, series or episode with the user's state; episodes name their series and numbers.
    internal sealed class MediaServerItem
    {
        public MediaServerItem(string id, MediaServerItemKind kind, string? tmdbId, string? imdbId, string? tvdbId, string? seriesId, int? season, int? episode,
            long positionMs, bool isPlayed, long? durationMs)
        {
            Id = id;
            Kind = kind;
            TmdbId = tmdbId;
            ImdbId = imdbId;
            TvdbId = tvdbId;
            SeriesId = seriesId;
            Season = season;
            Episode = episode;
            PositionMs = positionMs;
            IsPlayed = isPlayed;
            DurationMs = durationMs;
        }

        public string Id { get; }
        public MediaServerItemKind Kind { get; }
        public string? TmdbId { get; }
        public string? ImdbId { get; }
        public string? TvdbId { get; }
        public string? SeriesId { get; }
        public int? Season { get; }
        public int? Episode { get; }
        public long PositionMs { get; }
        public bool IsPlayed { get; }
        public long? DurationMs { get; }
    }

    internal sealed class MediaServerItemPage
    {
        public MediaServerItemPage(IReadOnlyList<MediaServerItem> items, int totalSize)
        {
            Items = items;
            TotalSize = totalSize;
        }

        public IReadOnlyList<MediaServerItem> Items { get; }
        public int TotalSize { get; }
    }
}
