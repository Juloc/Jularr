using System.Text.Json.Serialization;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;

namespace Jularr.Web.Features.Acquisition.Search;

/// <summary>How far one search goes. Automatic search defaults to Normal; Deep is explicit troubleshooting and never a background default.</summary>
public enum SearchDepth
{
    /// <summary>Only the strongest structured queries, one page.</summary>
    Fast,

    /// <summary>The default: the strong queries first and bounded expansion while too few usable candidates exist.</summary>
    Normal,

    /// <summary>Aliases, fallbacks and extra pages.</summary>
    Deep
}

/// <summary>Who asked: Wanted (automatic) or an owner in Manual Search. Each indexer decides separately whether it takes part in either.</summary>
public enum SearchPurpose
{
    Automatic,
    Interactive
}

/// <summary>
/// The canonical target of one search, independent of any indexer: what the media is called, which provider identities and numbering
/// exist for it. Every media type fills the fields it has; the planner turns them into queries only as far as an indexer supports.
/// </summary>
public sealed record SearchIntent(MediaAcquisitionKind Kind, string Title)
{
    /// <summary>Alternate titles (localized, romanized, native), strongest first.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    public int? Year { get; init; }

    /// <summary>The author (Books, Light Novels) or artist (Music).</summary>
    public string? Creator { get; init; }

    /// <summary>Provider identities by lowercase key (<c>tmdb</c>, <c>tvdb</c>, <c>imdb</c>); only trustworthy ones belong here.</summary>
    public IReadOnlyDictionary<string, string> ExternalIds { get; init; } = new Dictionary<string, string>();

    public int? Season { get; init; }

    public int? Episode { get; init; }

    /// <summary>The absolute episode number canonical mapping evidence assigned (Anime).</summary>
    public int? AbsoluteEpisode { get; init; }

    public int? Volume { get; init; }

    public decimal? Chapter { get; init; }

    /// <summary>A title the numbering does not apply to, such as the album a Music search is for.</summary>
    public string? Subtitle { get; init; }
}

/// <summary>Per-search switches; the defaults are the automatic Normal search.</summary>
public sealed record SearchOptions
{
    public SearchDepth Depth { get; init; } = SearchDepth.Normal;

    public SearchPurpose Purpose { get; init; } = SearchPurpose.Automatic;

    /// <summary>
    /// The media type's own count of candidates that are identity-valid for the target. Expansion stops once enough distinct usable
    /// candidates exist; without a counter every distinct candidate counts, which only stops expansion early.
    /// </summary>
    public Func<IReadOnlyList<ProwlarrReleaseCandidate>, int>? UsableCount { get; init; }

    /// <summary>Restricts the search to these indexer entries (the profile's allowed sources); null searches every enabled entry.</summary>
    public IReadOnlyCollection<Guid>? AllowedEntryIds { get; init; }

    /// <summary>Indexer entries whose releases win a tie against the same release from another entry (a profile's preferred sources).</summary>
    public IReadOnlyCollection<Guid>? PreferredEntryIds { get; init; }

    /// <summary>Indexer entries that are searched only when every other entry of the search returned no release (a profile's fallback-only sources).</summary>
    public IReadOnlyCollection<Guid>? FallbackOnlyEntryIds { get; init; }

    /// <summary>
    /// Applies an Acquisition Profile's source policy on top of what the caller already restricts: an allow list intersects with it (so a restriction
    /// can only narrow), and an empty intersection stays empty rather than becoming "everything".
    /// </summary>
    public SearchOptions WithSourcePolicy(Quality.AcquisitionSourcePolicy policy) =>
        this with
        {
            AllowedEntryIds = policy.IsRestricted ? AllowedEntryIds is null ? policy.AllowedEntryIds : [.. AllowedEntryIds.Intersect(policy.AllowedEntryIds)] : AllowedEntryIds,
            PreferredEntryIds = policy.PreferredEntryIds.Length > 0 ? policy.PreferredEntryIds : PreferredEntryIds,
            FallbackOnlyEntryIds = policy.FallbackOnlyEntryIds.Length > 0 ? policy.FallbackOnlyEntryIds : FallbackOnlyEntryIds
        };

    /// <summary>Overrides the Prowlarr per-indexer restriction of every Prowlarr entry.</summary>
    public IReadOnlyList<int>? ProwlarrIndexerIds { get; init; }

    /// <summary>An explicit, authorized refresh: reads past the short-lived evidence cache.</summary>
    public bool Refresh { get; init; }

    /// <summary>Shortens the per-indexer timeout of the depth for this search; it can never lengthen it.</summary>
    public TimeSpan? IndexerTimeout { get; init; }
}

/// <summary>The bounds of one search pass; every one of them is explicit so a slow or broken indexer can only cost its own share.</summary>
public sealed record SearchBudget(
    int MaxConcurrentIndexers,
    TimeSpan IndexerTimeout,
    int MaxQueryVariants,
    int MaxPages,
    int MaxResults,
    int TargetUsable,
    int MaxTier)
{
    public static SearchBudget For(SearchDepth depth) =>
        depth switch
        {
            SearchDepth.Fast => new(4, TimeSpan.FromSeconds(12), 2, 1, 300, 1, 0),
            SearchDepth.Deep => new(4, TimeSpan.FromSeconds(45), 24, 4, 1200, int.MaxValue, 2),
            _ => new(4, TimeSpan.FromSeconds(25), 8, 2, 600, 3, 1)
        };
}

/// <summary>One query of a plan: where it sits in the ladder, what it asks for and how Manual Search words it.</summary>
public sealed record PlannedQuery(
    string Stage,
    int Tier,
    IndexerSearchMode Mode,
    string? Text,
    IReadOnlyList<KeyValuePair<string, string>> Parameters,
    string Provenance,
    bool AnyCategory = false)
{
    /// <summary>The identity of the query for the evidence cache and for skipping repeats; it carries no credentials.</summary>
    public string Key =>
        $"{Mode}|{Text?.ToLowerInvariant()}|{string.Join('&', Parameters.Select(pair => $"{pair.Key}={pair.Value}"))}|{(AnyCategory ? "any" : "cat")}";
}

/// <summary>The query that found a candidate, kept so Manual Search can say "Found via TVDB + S01E03 · Indexer A".</summary>
public sealed record QueryProvenance(string IndexerName, string Stage, string Description, string QueryText);

/// <summary>One indexer that returned a logical candidate. Another source can be grabbed when this one fails; the download URI never reaches a response.</summary>
public sealed record ReleaseSourceOption(
    string Indexer,
    Guid? EntryId,
    int Priority,
    string? Guid,
    [property: JsonIgnore] Uri? DownloadUri,
    string? InfoUrl,
    string Identity);

/// <summary>What happened to one indexer in a search: reachable and useful, empty, or the reason it could not contribute.</summary>
public enum IndexerSearchState
{
    Searched,
    NoResults,
    Unavailable,
    AuthenticationFailed,
    RateLimited,
    TimedOut,
    Skipped
}

public sealed record IndexerSearchOutcome(
    Guid EntryId,
    string IndexerName,
    IndexerSearchState State,
    int QueriesRun,
    int RawResults,
    string? Message = null,
    DateTimeOffset? RetryAfter = null);

/// <summary>One executed query for the Search Trace.</summary>
public sealed record SearchTraceLine(
    string IndexerName,
    string Stage,
    string Provenance,
    string QueryText,
    int Page,
    int Results,
    int NewCandidates,
    bool FromCache);

/// <summary>
/// The evidence one search produced: logical candidates (equivalent releases of several indexers merged, every source kept), a
/// per-indexer outcome and a trace. It says nothing about whether a candidate is acceptable.
/// </summary>
public sealed record AcquisitionSearchResult(
    IReadOnlyList<ProwlarrReleaseCandidate> Releases,
    IReadOnlyList<IndexerSearchOutcome> Outcomes,
    IReadOnlyList<SearchTraceLine> Trace,
    int RawResultCount)
{
    public static AcquisitionSearchResult Empty { get; } = new([], [], [], 0);

    /// <summary>
    /// Set when the profile's source policy left no enabled indexer to ask: nothing was searched and nothing was widened. It is an unavailable source, not a
    /// statement about the media, so it never counts as a failed search.
    /// </summary>
    public string? SourcePolicyBlock { get; init; }

    /// <summary>The per-indexer problems in the shape the callers show: a skipped, failed or rate-limited indexer, with the query that failed.</summary>
    public IReadOnlyList<IndexerSearchWarning> Warnings =>
        [.. (SourcePolicyBlock is null ? Array.Empty<IndexerSearchWarning>() : new[] { new IndexerSearchWarning("Acquisition Profile", string.Empty, SourcePolicyBlock) }),
            .. Outcomes.Where(outcome => outcome.State is not (IndexerSearchState.Searched or IndexerSearchState.NoResults) || outcome.Message is not null)
            .Select(outcome => new IndexerSearchWarning(outcome.IndexerName, string.Empty, outcome.Message ?? outcome.State.ToString()))];

    /// <summary>True when no indexer could answer at all, so an empty result says nothing about the media.</summary>
    public bool EveryIndexerFailed =>
        SourcePolicyBlock is not null
        || Outcomes.Count > 0 && Outcomes.All(outcome => outcome.State is IndexerSearchState.Unavailable or IndexerSearchState.AuthenticationFailed or IndexerSearchState.RateLimited or IndexerSearchState.TimedOut);
}
