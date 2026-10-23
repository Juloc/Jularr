using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Plex
{
    internal sealed class PlexServerConnection
    {
        public PlexServerConnection(Uri baseUrl, string token)
        {
            BaseUrl = baseUrl;
            Token = token;
        }

        public Uri BaseUrl { get; }

        [JsonIgnore]
        public string Token { get; }
    }

    internal enum PlexServerFailure : byte
    {
        Unavailable,
        Unauthorized,
        InvalidResponse
    }

    internal sealed class PlexServerException : Exception
    {
        public PlexServerException(PlexServerFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public PlexServerFailure Failure { get; }
    }

    internal enum PlexSectionKind : byte
    {
        Movie,
        Show
    }

    internal sealed class PlexSection
    {
        public PlexSection(string key, PlexSectionKind kind, string title)
        {
            Key = key;
            Kind = kind;
            Title = title;
        }

        public string Key { get; }
        public PlexSectionKind Kind { get; }
        public string Title { get; }
    }

    internal sealed class PlexServerInfo
    {
        public PlexServerInfo(string machineIdentifier, string? version, IReadOnlyList<PlexSection> sections)
        {
            MachineIdentifier = machineIdentifier;
            Version = version;
            Sections = sections;
        }

        public string MachineIdentifier { get; }
        public string? Version { get; }

        // Movie and show libraries only.
        public IReadOnlyList<PlexSection> Sections { get; }
    }

    internal sealed class PlexItem
    {
        public PlexItem(string ratingKey, string title, int? year, string? tmdbId, string? imdbId, string? tvdbId)
        {
            RatingKey = ratingKey;
            Title = title;
            Year = year;
            TmdbId = tmdbId;
            ImdbId = imdbId;
            TvdbId = tvdbId;
        }

        public string RatingKey { get; }
        public string Title { get; }
        public int? Year { get; }
        public string? TmdbId { get; }
        public string? ImdbId { get; }
        public string? TvdbId { get; }
    }

    internal sealed class PlexItemPage
    {
        public PlexItemPage(IReadOnlyList<PlexItem> items, int totalSize)
        {
            Items = items;
            TotalSize = totalSize;
        }

        public IReadOnlyList<PlexItem> Items { get; }
        public int TotalSize { get; }
    }
}
