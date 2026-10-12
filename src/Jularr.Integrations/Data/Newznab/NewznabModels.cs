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

    internal sealed record NewznabConnection(string BaseUrl, [property: JsonIgnore] string ApiKey);

    // What an indexer reported it can search with; structured parameters are only sent when advertised.
    internal sealed record NewznabCapabilities(
        DateTimeOffset RefreshedAt,
        IReadOnlyDictionary<NewznabSearchMode, string[]> Modes,
        int? MaximumLimit = null,
        int[]? Categories = null,
        string? ServerTitle = null,
        string? Version = null)
    {
        public static NewznabCapabilities TextOnly(DateTimeOffset refreshedAt) => new(refreshedAt, new Dictionary<NewznabSearchMode, string[]> { [NewznabSearchMode.Search] = ["q"] });

        public bool Supports(NewznabSearchMode mode) => Modes.ContainsKey(mode);

        public bool Supports(NewznabSearchMode mode, string parameter) => Modes.TryGetValue(mode, out var parameters) && parameters.Contains(parameter, StringComparer.OrdinalIgnoreCase);

        // Parent categories (multiples of 1000) include their sub categories.
        public bool Offers(int category) =>
            Categories is not { Length: > 0 }
            || Categories.Contains(category)
            || (category % 1000 == 0 ? Categories.Any(offered => offered / 1000 == category / 1000) : category < 100000 && Categories.Contains(category / 1000 * 1000));
    }

    internal sealed record NewznabQuery(NewznabSearchMode Mode, string? Text, IReadOnlyList<KeyValuePair<string, string>> Parameters, IReadOnlyList<int> Categories, int Limit, int Offset = 0);

    // One result item as the indexer delivered it; release parsing belongs to the caller.
    internal sealed record NewznabItem(string Title, string? Guid, [property: JsonIgnore] Uri? DownloadUri, string? InfoUrl, long? SizeBytes, DateTimeOffset? PublishedAt);

    internal enum NewznabFailure : byte
    {
        Unavailable = 1,
        AuthenticationFailed = 2,
        RateLimited = 3,
        ParametersRejected = 4,
        InvalidResponse = 5
    }

    // Messages never contain the API key.
    internal sealed class NewznabException(NewznabFailure failure, string message, TimeSpan? retryAfter = null, Exception? innerException = null) : Exception(message, innerException)
    {
        public NewznabFailure Failure { get; } = failure;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
