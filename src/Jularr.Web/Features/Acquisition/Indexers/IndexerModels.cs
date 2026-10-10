using System.Text.Json.Serialization;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Prowlarr;

namespace Jularr.Web.Features.Acquisition.Indexers;

/// <summary>
/// Kind of indexer connection. Prowlarr aggregates other indexers itself;
/// Newznab is a direct connection to a single usenet indexer. Jularr is
/// usenet-only: torrent indexers (Torznab) are intentionally unsupported.
/// </summary>
public enum IndexerType
{
    Prowlarr,
    Newznab
}

public enum IndexerCheckState
{
    NotChecked,
    Valid,
    NoResults,
    ParametersRejected,
    AuthenticationFailed,
    RateLimited,
    Unavailable,
    InvalidResponse
}

/// <summary>The outcome of one bounded validation search of one search function.</summary>
public sealed record IndexerCheck(IndexerCheckState State, string? Message = null);

/// <summary>
/// The proof gathered by setup and refresh, kept apart from the owner's settings: whether the connection and key work, which search functions
/// answered a real request, and why the last refresh failed when it did. A failed refresh never replaces earlier capabilities or checks.
/// </summary>
public sealed record IndexerVerification(
    DateTimeOffset CheckedAt,
    bool Connected,
    bool Authenticated,
    IReadOnlyDictionary<IndexerSearchMode, IndexerCheck> Searches,
    string? RefreshError = null,
    DateTimeOffset? RefreshFailedAt = null,
    IReadOnlyDictionary<MediaAcquisitionKind, IndexerCheck>? KindChecks = null)
{
    /// <summary>The functions that answered a request with a parsable result, empty or not.</summary>
    public bool Answered(IndexerSearchMode mode) => Searches.TryGetValue(mode, out var check) && check.State is IndexerCheckState.Valid or IndexerCheckState.NoResults;

    public bool Rejected(IndexerSearchMode mode) => Searches.TryGetValue(mode, out var check) && check.State == IndexerCheckState.ParametersRejected;
}

/// <summary>
/// One canonical indexer connection's settings. <see cref="IndexerIds"/> is
/// only meaningful for <see cref="IndexerType.Prowlarr"/> (Prowlarr's own
/// per-indexer restriction); Newznab ignores it.
/// </summary>
public sealed record IndexerSettings(
    string BaseUrl,
    int[] Categories,
    int[] IndexerIds,
    int SearchLimit,
    int[]? BookCategories = null)
{
    /// <summary>Newznab categories 7020 (EBook) and 7000 (Books) unless the owner set others.</summary>
    public static readonly int[] DefaultBookCategories = [7020, 7000];

    public int[] EffectiveBookCategories =>
        BookCategories is { Length: > 0 } configured ? configured : DefaultBookCategories;

    /// <summary>What the indexer reported it can search with; null until its caps were read. Refreshed on test, never edited by hand.</summary>
    public IndexerCapabilities? Capabilities { get; init; }

    /// <summary>What setup and the last refresh proved about the indexer; null for an entry that was never checked.</summary>
    public IndexerVerification? Verification { get; init; }

    /// <summary>Whether Wanted/automatic search may use this indexer. A manual-only indexer is valid.</summary>
    public bool AutomaticSearch { get; init; } = true;

    /// <summary>Whether Manual Search may use this indexer.</summary>
    public bool InteractiveSearch { get; init; } = true;

    /// <summary>The media types this indexer is searched for; null searches it for every media type.</summary>
    public MediaAcquisitionKind[]? MediaKinds { get; init; }

    /// <summary>
    /// The Newznab categories the owner chose for a media type of this indexer, by the media type's name; a type without an entry uses its default
    /// (<see cref="SearchPlanner.Categories"/>). Parent and sub categories can be mixed, and an indexer's own custom categories (100000 and up) work too.
    /// </summary>
    public Dictionary<string, int[]>? CategoriesByKind { get; init; }

    /// <summary>The categories the owner configured for one media type, or null to use the media type's default.</summary>
    public int[]? CategoriesFor(MediaAcquisitionKind kind) =>
        CategoriesByKind is { } configured && configured.TryGetValue(AcquisitionAccessNames.Kind(kind), out var ids) && ids.Length > 0 ? ids : null;

    public static IndexerSettings CreateDefault(string baseUrl, IndexerType type) =>
        new(
            baseUrl,
            Categories: type == IndexerType.Prowlarr ? [5000, 5070] : [5070],
            IndexerIds: [],
            SearchLimit: 100);
}

/// <summary>
/// One entry of the canonical indexer list. The API key is protected at
/// rest; <see cref="Priority"/> is lower-is-first, matching download client
/// priority.
/// </summary>
public sealed record IndexerEntry(
    Guid Id,
    string Name,
    IndexerType Type,
    bool Enabled,
    int Priority,
    IndexerSettings Settings,
    [property: JsonIgnore] string ApiKey);

/// <summary>
/// The answer of a connection test. <see cref="State"/> tells why a test failed, so the page can say whether the key, the address, the API or the
/// indexer's availability is the problem; <see cref="Valid"/> means the caps document was read.
/// </summary>
public sealed record IndexerConnectionTestResult(
    bool Success,
    string? Version = null,
    string? Error = null,
    IndexerCapabilities? Capabilities = null,
    IndexerCheckState State = IndexerCheckState.Valid);

/// <summary>
/// One query the planner decided on, as an indexer receives it: the Newznab function, the free text (empty for a pure ID search), the
/// structured parameters that function accepts and the page to read. <paramref name="Latest"/> asks for the newest releases without any text
/// or parameter; only validation uses it, to prove that a function answers.
/// </summary>
public sealed record IndexerSearchQuery(
    string Query,
    IndexerSearchMode Mode = IndexerSearchMode.Search,
    IReadOnlyList<KeyValuePair<string, string>>? Parameters = null,
    int Offset = 0,
    int? Limit = null,
    bool Latest = false);

public sealed record IndexerSearchWarning(
    string IndexerName,
    string Query,
    string Message);

/// <summary>
/// One indexer implementation. <see cref="AcquisitionCandidate"/> is the
/// one release-candidate model every indexer type and the quality scorer
/// share; it already carries a per-result <c>Protocol</c> (usenet/torrent).
/// </summary>
public interface IIndexer
{
    IndexerType Type { get; }

    Task<IndexerConnectionTestResult> TestAsync(
        IndexerEntry entry,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(
        IndexerEntry entry,
        IndexerSearchQuery query,
        CancellationToken cancellationToken);
}

public class IndexerException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public enum IndexerRejection
{
    MissingParameter,
    IncorrectParameter,
    UnsupportedFunction
}

/// <summary>
/// The indexer answered, but refused the request shape (Newznab error 200-203, also delivered as HTTP 200). Searching again with the same function and
/// parameters cannot succeed; <see cref="RequestShape"/> names what was sent without the API key.
/// </summary>
public sealed class IndexerRequestRejectedException(string message, IndexerRejection reason, IndexerSearchMode mode, string requestShape, string? providerMessage)
    : IndexerException(message)
{
    public IndexerRejection Reason { get; } = reason;

    public IndexerSearchMode Mode { get; } = mode;

    public string RequestShape { get; } = requestShape;

    public string? ProviderMessage { get; } = providerMessage;
}

/// <summary>The indexer rejected the credentials; searching it again cannot succeed until the owner fixes the key.</summary>
public sealed class IndexerAuthenticationException(string message) : IndexerException(message);

/// <summary>The indexer refused the request because a query/grab limit is reached; it may be asked again after <see cref="RetryAfter"/>.</summary>
public sealed class IndexerRateLimitedException(string message, TimeSpan? retryAfter) : IndexerException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
