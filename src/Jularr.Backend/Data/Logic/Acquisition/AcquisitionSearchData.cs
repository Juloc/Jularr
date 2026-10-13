using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Integrations.Data.Newznab;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // A localizable explanation: a stable code and an optional data value.
    internal sealed class ReleaseReason
    {
        public ReleaseReason(string code, string? value = null)
        {
            Code = code;
            Value = value;
        }

        public string Code { get; }
        public string? Value { get; }
    }

    // The canonical target of one search, independent of any indexer.
    internal sealed class SearchIntent
    {
        public SearchIntent(AcquisitionKind kind, string title)
        {
            Kind = kind;
            Title = title;
        }

        public AcquisitionKind Kind { get; }
        public string Title { get; }
        public IReadOnlyList<string> Aliases { get; init; } = [];
        public int? Year { get; init; }
        public string? Creator { get; init; }
        public IReadOnlyDictionary<string, string> ExternalIds { get; init; } = new Dictionary<string, string>();
        public int? Season { get; init; }
        public int? Episode { get; init; }
        public int? AbsoluteEpisode { get; init; }
        public int? Volume { get; init; }
        public decimal? Chapter { get; init; }
    }

    // Explicit bounds of one search pass, so a slow indexer only costs its own share.
    internal sealed class SearchBudget
    {
        public SearchBudget(int maxConcurrentIndexers, TimeSpan indexerTimeout, int maxQueryVariants, int maxPages, int maxResults, int targetUsable, int maxTier)
        {
            MaxConcurrentIndexers = maxConcurrentIndexers;
            IndexerTimeout = indexerTimeout;
            MaxQueryVariants = maxQueryVariants;
            MaxPages = maxPages;
            MaxResults = maxResults;
            TargetUsable = targetUsable;
            MaxTier = maxTier;
        }

        public int MaxConcurrentIndexers { get; }
        public TimeSpan IndexerTimeout { get; }
        public int MaxQueryVariants { get; }
        public int MaxPages { get; }
        public int MaxResults { get; }
        public int TargetUsable { get; }
        public int MaxTier { get; }

        public static SearchBudget For(SearchDepth depth) => depth switch
        {
            SearchDepth.Fast => new(4, TimeSpan.FromSeconds(12), 2, 1, 300, 1, 0),
            SearchDepth.Deep => new(4, TimeSpan.FromSeconds(45), 24, 4, 1200, int.MaxValue, 2),
            _ => new(4, TimeSpan.FromSeconds(25), 8, 2, 600, 3, 1)
        };
    }

    // One query of a plan: its rung in the ladder and what it asks for.
    internal sealed class PlannedQuery
    {
        public PlannedQuery(string stage, int tier, NewznabSearchMode mode, string? text, IReadOnlyList<KeyValuePair<string, string>> parameters, string provenance, bool anyCategory = false)
        {
            Stage = stage;
            Tier = tier;
            Mode = mode;
            Text = text;
            Parameters = parameters;
            Provenance = provenance;
            AnyCategory = anyCategory;
        }

        public string Stage { get; }
        public int Tier { get; }
        public NewznabSearchMode Mode { get; }
        public string? Text { get; }
        public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; }
        public string Provenance { get; }
        public bool AnyCategory { get; }

        public string Key => $"{Mode}|{Text?.ToLowerInvariant()}|{string.Join('&', Parameters.Select(pair => $"{pair.Key}={pair.Value}"))}|{(AnyCategory ? "any" : "cat")}";
    }

    // One normalized release found by an indexer; the download link never leaves the server.
    internal sealed class ReleaseOffer
    {
        public ReleaseOffer(string title, long connectionId, string connectionName, int connectionPriority, string? guid, Uri? downloadUri, string? infoUrl, long? sizeBytes, DateTimeOffset? publishedAt, IReadOnlyList<string> stages)
        {
            Title = title;
            ConnectionId = connectionId;
            ConnectionName = connectionName;
            ConnectionPriority = connectionPriority;
            Guid = guid;
            DownloadUri = downloadUri;
            InfoUrl = infoUrl;
            SizeBytes = sizeBytes;
            PublishedAt = publishedAt;
            Stages = stages;
        }

        public string Title { get; }
        public long ConnectionId { get; }
        public string ConnectionName { get; }
        public int ConnectionPriority { get; }
        public string? Guid { get; }
        public Uri? DownloadUri { get; }
        public string? InfoUrl { get; }
        public long? SizeBytes { get; }
        public DateTimeOffset? PublishedAt { get; }
        public IReadOnlyList<string> Stages { get; }

        public string Identity => ConnectionId + ":" + (Guid ?? Title);
    }

    // What a media type concluded about one release.
    internal sealed class ReleaseJudgement<TMatch>
    {
        public ReleaseJudgement(TMatch match, ReleaseInfo? parsed, ReleaseIdentityEvidence evidence, SelectionCoverage coverage, string? safetyRejection)
        {
            Match = match;
            Parsed = parsed;
            Evidence = evidence;
            Coverage = coverage;
            SafetyRejection = safetyRejection;
        }

        public TMatch Match { get; }
        public ReleaseInfo? Parsed { get; }
        public ReleaseIdentityEvidence Evidence { get; }
        public SelectionCoverage Coverage { get; }
        public string? SafetyRejection { get; }
        public int ContextScore { get; init; }
        public IReadOnlyList<string>? Languages { get; init; }
    }

    // One canonical unit a search targets: an episode in its season or a volume; movies and books have none.
    internal sealed class TargetUnit
    {
        public TargetUnit(long id, int? parentNumber, int number, bool hasFile)
        {
            Id = id;
            ParentNumber = parentNumber;
            Number = number;
            HasFile = hasFile;
        }

        public long Id { get; }
        public int? ParentNumber { get; }
        public int Number { get; }
        public bool HasFile { get; }
    }

    // The units of a Work a search is for.
    internal sealed class UnitSet
    {
        public UnitSet(IReadOnlyList<TargetUnit> all, IReadOnlyList<TargetUnit> wanted)
        {
            All = all;
            Wanted = wanted;
        }

        public IReadOnlyList<TargetUnit> All { get; }
        public IReadOnlyList<TargetUnit> Wanted { get; }
        public static UnitSet Empty { get; } = new([], []);
    }

    // The group (season) and unit a search is for and the units it may cover; no unit number means the whole group.
    internal sealed class UnitScope
    {
        public UnitScope(int? parentNumber, int? unitNumber, TargetUnit unit, UnitSet units)
        {
            ParentNumber = parentNumber;
            UnitNumber = unitNumber;
            Unit = unit;
            Units = units;
        }

        public int? ParentNumber { get; }
        public int? UnitNumber { get; }
        public TargetUnit Unit { get; }
        public UnitSet Units { get; }
    }

    internal sealed class WantedSearchResult
    {
        public WantedSearchResult(SelectionResult selection, IReadOnlyDictionary<string, ReleaseOffer> offers, bool indexersUnavailable, string outcomeKey)
        {
            Selection = selection;
            Offers = offers;
            IndexersUnavailable = indexersUnavailable;
            OutcomeKey = outcomeKey;
        }

        public SelectionResult Selection { get; }
        public IReadOnlyDictionary<string, ReleaseOffer> Offers { get; }
        public bool IndexersUnavailable { get; }
        public string OutcomeKey { get; }
    }
}
