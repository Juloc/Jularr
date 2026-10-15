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

        // The indexer has a key the API does not show; without one it is open.
        public bool HasHiddenKey { get; init; }
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
        public ServarrTitle(string title, int? year, int? tmdbId, bool isMonitored, bool isMissing, string? externalId = null, string? creator = null, int? qualityProfileId = null)
        {
            QualityProfileId = qualityProfileId;
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

        // The quality profile the title used there.
        public int? QualityProfileId { get; }

        // The MusicBrainz release group of an album; artist or author.
        public string? ExternalId { get; }
        public string? Creator { get; }
    }

    // One quality of a profile with its source and resolution, allowed alone or through its group.
    internal sealed class ServarrQuality
    {
        public ServarrQuality(string? source, int resolution, bool isAllowed, bool isCutoff)
        {
            Source = source;
            Resolution = resolution;
            IsAllowed = isAllowed;
            IsCutoff = isCutoff;
        }

        public string? Source { get; }
        public int Resolution { get; }
        public bool IsAllowed { get; }
        public bool IsCutoff { get; }
    }

    internal sealed class ServarrQualityProfile
    {
        public ServarrQualityProfile(int id, string name, bool upgradeAllowed, IReadOnlyList<ServarrQuality> qualities, IReadOnlyDictionary<int, int> formatScores, int minimumFormatScore)
        {
            Id = id;
            Name = name;
            UpgradeAllowed = upgradeAllowed;
            Qualities = qualities;
            FormatScores = formatScores;
            MinimumFormatScore = minimumFormatScore;
        }

        public int Id { get; }
        public string Name { get; }
        public bool UpgradeAllowed { get; }
        public IReadOnlyList<ServarrQuality> Qualities { get; }

        // Score per custom format id.
        public IReadOnlyDictionary<int, int> FormatScores { get; }
        public int MinimumFormatScore { get; }
    }

    internal sealed class ServarrFormatCondition
    {
        public ServarrFormatCondition(string implementation, bool negate, bool required, string? value)
        {
            Implementation = implementation;
            Negate = negate;
            Required = required;
            Value = value;
        }

        public string Implementation { get; }
        public bool Negate { get; }
        public bool Required { get; }
        public string? Value { get; }
    }

    internal sealed class ServarrCustomFormat
    {
        public ServarrCustomFormat(int id, string name, IReadOnlyList<ServarrFormatCondition> conditions)
        {
            Id = id;
            Name = name;
            Conditions = conditions;
        }

        public int Id { get; }
        public string Name { get; }
        public IReadOnlyList<ServarrFormatCondition> Conditions { get; }
    }
}
