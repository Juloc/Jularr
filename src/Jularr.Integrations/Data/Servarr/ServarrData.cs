namespace Jularr.Integrations.Data.Servarr
{
    internal enum ServarrFailure : byte
    {
        Unavailable,
        Unauthorized,
        InvalidResponse
    }

    internal sealed class ServarrException : Exception
    {
        public ServarrException(ServarrFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public ServarrFailure Failure { get; }
    }

    // Prowlarr speaks API v1, Sonarr and Radarr v3.
    internal sealed class ServarrConnection
    {
        public ServarrConnection(Uri baseUrl, string apiKey, int apiVersion)
        {
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            ApiVersion = apiVersion;
        }

        public Uri BaseUrl { get; }
        public string ApiKey { get; }
        public int ApiVersion { get; }
    }

    // A Newznab indexer; current versions hide the key, which then stays empty.
    internal sealed class ServarrIndexer
    {
        public ServarrIndexer(string name, string baseUrl, string? apiKey, bool isEnabled, int priority, int[] categories)
        {
            Name = name;
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            IsEnabled = isEnabled;
            Priority = priority;
            Categories = categories;
        }

        public string Name { get; }
        public string BaseUrl { get; }
        public string? ApiKey { get; }
        public bool IsEnabled { get; }
        public int Priority { get; }
        public int[] Categories { get; }
    }

    internal sealed class ServarrClient
    {
        public ServarrClient(string name, string baseUrl, string? apiKey, bool isEnabled, int priority, string? category)
        {
            Name = name;
            BaseUrl = baseUrl;
            ApiKey = apiKey;
            IsEnabled = isEnabled;
            Priority = priority;
            Category = category;
        }

        public string Name { get; }
        public string BaseUrl { get; }
        public string? ApiKey { get; }
        public bool IsEnabled { get; }
        public int Priority { get; }
        public string? Category { get; }
    }

    // A movie, series, album or book the old tool watched; missing ones become wanted.
    internal sealed class ServarrTitle
    {
        public ServarrTitle(string title, int? year, int? tmdbId, bool isMonitored, bool isMissing, string? externalId = null, string? creator = null)
        {
            ExternalId = externalId;
            Creator = creator;
            Title = title;
            Year = year;
            TmdbId = tmdbId;
            IsMonitored = isMonitored;
            IsMissing = isMissing;
        }

        public string Title { get; }
        public int? Year { get; }
        public int? TmdbId { get; }
        public bool IsMonitored { get; }
        public bool IsMissing { get; }

        // The MusicBrainz release group of an album; artist or author.
        public string? ExternalId { get; }
        public string? Creator { get; }
    }
}
