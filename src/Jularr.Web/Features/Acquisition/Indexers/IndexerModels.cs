using System.Text.Json.Serialization;
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

    /// <summary>Whether Wanted/automatic search may use this indexer. A manual-only indexer is valid.</summary>
    public bool AutomaticSearch { get; init; } = true;

    /// <summary>Whether Manual Search may use this indexer.</summary>
    public bool InteractiveSearch { get; init; } = true;

    /// <summary>The media types this indexer is searched for; null searches it for every media type.</summary>
    public MediaAcquisitionKind[]? MediaKinds { get; init; }

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

public sealed record IndexerConnectionTestResult(
    bool Success,
    string? Version = null,
    string? Error = null,
    IndexerCapabilities? Capabilities = null);

/// <summary>
/// One query the planner decided on, as an indexer receives it: the Newznab function, the free text (empty for a pure ID search), the
/// structured parameters that function accepts and the page to read.
/// </summary>
public sealed record IndexerSearchQuery(
    string Query,
    IndexerSearchMode Mode = IndexerSearchMode.Search,
    IReadOnlyList<KeyValuePair<string, string>>? Parameters = null,
    int Offset = 0,
    int? Limit = null);

public sealed record IndexerSearchWarning(
    string IndexerName,
    string Query,
    string Message);

/// <summary>
/// One indexer implementation. <see cref="ProwlarrReleaseCandidate"/> is the
/// one release-candidate model every indexer type and the quality scorer
/// share; it already carries a per-result <c>Protocol</c> (usenet/torrent).
/// </summary>
public interface IIndexer
{
    IndexerType Type { get; }

    Task<IndexerConnectionTestResult> TestAsync(
        IndexerEntry entry,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
        IndexerEntry entry,
        IndexerSearchQuery query,
        CancellationToken cancellationToken);
}

public class IndexerException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The indexer rejected the credentials; searching it again cannot succeed until the owner fixes the key.</summary>
public sealed class IndexerAuthenticationException(string message) : IndexerException(message);

/// <summary>The indexer refused the request because a query/grab limit is reached; it may be asked again after <see cref="RetryAfter"/>.</summary>
public sealed class IndexerRateLimitedException(string message, TimeSpan? retryAfter) : IndexerException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
