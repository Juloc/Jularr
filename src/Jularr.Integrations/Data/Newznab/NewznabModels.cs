using System.Net;
using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Newznab
{
    // A Newznab search function (the t= parameter); each accepts its own parameters.
    internal enum NewznabSearchMode : byte
    {
        Search = 1,
        TvSearch = 2,
        Movie = 3,
        Book = 4,
        Music = 5
    }

    internal sealed class NewznabConnection
    {
        public NewznabConnection(string baseUrl, string apiKey)
        {
            BaseUrl = baseUrl;
            ApiKey = apiKey;
        }

        public string BaseUrl { get; }

        [JsonIgnore]
        public string ApiKey { get; }
    }

    // What an indexer reported it can search with; structured parameters are only sent when advertised.
    internal sealed class NewznabCapabilities
    {
        public NewznabCapabilities(DateTimeOffset refreshedAt, IReadOnlyDictionary<NewznabSearchMode, string[]> modes, int? maximumLimit = null, int[]? categories = null, string? serverTitle = null, string? version = null)
        {
            RefreshedAt = refreshedAt;
            Modes = modes;
            MaximumLimit = maximumLimit;
            Categories = categories;
            ServerTitle = serverTitle;
            Version = version;
        }

        public DateTimeOffset RefreshedAt { get; }
        public IReadOnlyDictionary<NewznabSearchMode, string[]> Modes { get; }
        public int? MaximumLimit { get; }
        public int[]? Categories { get; }
        public string? ServerTitle { get; }
        public string? Version { get; }

        public static NewznabCapabilities TextOnly(DateTimeOffset refreshedAt) => new(refreshedAt, new Dictionary<NewznabSearchMode, string[]> { [NewznabSearchMode.Search] = ["q"] });

        public bool Supports(NewznabSearchMode mode) => Modes.ContainsKey(mode);

        public bool Supports(NewznabSearchMode mode, string parameter) => Modes.TryGetValue(mode, out var parameters) && parameters.Contains(parameter, StringComparer.OrdinalIgnoreCase);

        // Parent categories (multiples of 1000) include their sub categories.
        public bool Offers(int category) =>
            Categories is not { Length: > 0 }
            || Categories.Contains(category)
            || (category % 1000 == 0 ? Categories.Any(offered => offered / 1000 == category / 1000) : category < 100000 && Categories.Contains(category / 1000 * 1000));
    }

    internal sealed class NewznabQuery
    {
        public NewznabQuery(NewznabSearchMode mode, string? text, IReadOnlyList<KeyValuePair<string, string>> parameters, IReadOnlyList<int> categories, int limit, int offset = 0)
        {
            Mode = mode;
            Text = text;
            Parameters = parameters;
            Categories = categories;
            Limit = limit;
            Offset = offset;
        }

        public NewznabSearchMode Mode { get; }
        public string? Text { get; }
        public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; }
        public IReadOnlyList<int> Categories { get; }
        public int Limit { get; }
        public int Offset { get; }
    }

    // One result item as the indexer delivered it; release parsing belongs to the caller.
    internal sealed class NewznabItem
    {
        public NewznabItem(string title, string? guid, Uri? downloadUri, string? infoUrl, long? sizeBytes, DateTimeOffset? publishedAt)
        {
            Title = title;
            Guid = guid;
            DownloadUri = downloadUri;
            InfoUrl = infoUrl;
            SizeBytes = sizeBytes;
            PublishedAt = publishedAt;
        }

        public string Title { get; }
        public string? Guid { get; }

        [JsonIgnore]
        public Uri? DownloadUri { get; }
        public string? InfoUrl { get; }
        public long? SizeBytes { get; }
        public DateTimeOffset? PublishedAt { get; }
        public IReadOnlyList<string> IndexerFlags { get; init; } = [];
    }

    internal enum NewznabFailure : byte
    {
        Unavailable = 1,
        AuthenticationFailed = 2,
        RateLimited = 3,
        ParametersRejected = 4,
        InvalidResponse = 5,
        NotFound = 6
    }

    // Messages never contain the API key.
    internal sealed class NewznabException(NewznabFailure failure, string message, TimeSpan? retryAfter = null, Exception? innerException = null) : Exception(message, innerException)
    {
        public NewznabFailure Failure { get; } = failure;
        public TimeSpan? RetryAfter { get; } = retryAfter;
        public HttpStatusCode? StatusCode { get; init; }
    }
}
