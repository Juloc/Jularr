using System.Net.Http;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Search;

namespace Jularr.Web.Features.Acquisition.Indexers;

/// <summary>
/// The one executor of Automatic and Manual Search. It asks every participating, healthy indexer for the queries
/// <see cref="SearchPlanner"/> planned for that indexer's capabilities, inside explicit budgets: indexers run side by side up to a
/// bound, each with its own timeout, a bounded number of query variants and pages, and a rate-limited indexer is left alone until
/// it asked to be asked again. One failed or slow indexer only costs its own share; the others' results stay. What comes back is
/// evidence (merged logical candidates with every source and the query that found them), never a verdict.
/// </summary>
public sealed class IndexerSearchCoordinator(
    IReadOnlyDictionary<IndexerType, IIndexer> indexers,
    IndexerStore store,
    AcquisitionHealthStore health,
    ILogger<IndexerSearchCoordinator> logger,
    SearchEvidenceCache? evidence = null)
{
    private readonly SearchEvidenceCache cache = evidence ?? new SearchEvidenceCache();

    public async Task<bool> HasEnabledIndexerAsync(CancellationToken cancellationToken) =>
        (await store.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled);

    /// <summary>The ids of every currently enabled indexer entry, for callers that need to know whether a restriction would leave anything to search.</summary>
    public async Task<IReadOnlyList<Guid>> EnabledEntryIdsAsync(CancellationToken cancellationToken) =>
        (await store.LoadAllAsync(cancellationToken))
            .Where(entry => entry.Enabled)
            .Select(entry => entry.Id)
            .ToArray();

    /// <summary>Searches for one canonical target; the planner shapes the queries for every indexer.</summary>
    public Task<AcquisitionSearchResult> SearchAsync(SearchIntent intent, SearchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return ExecuteAsync(intent.Kind, capabilities => SearchPlanner.Plan(intent, capabilities, options.Depth), options, cancellationToken);
    }

    /// <summary>Searches with free text a person typed (Discover search) in the categories of a media type; there is no target to plan from.</summary>
    public Task<AcquisitionSearchResult> SearchTextAsync(MediaAcquisitionKind kind, IReadOnlyList<string> queries, SearchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);
        return ExecuteAsync(kind, _ => SearchPlanner.PlanText(queries), options, cancellationToken);
    }

    private async Task<AcquisitionSearchResult> ExecuteAsync(MediaAcquisitionKind kind, Func<IndexerCapabilities?, IReadOnlyList<PlannedQuery>> planFor, SearchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var budget = SearchBudget.For(options.Depth);
        var entries = (await store.LoadAllAsync(cancellationToken))
            .Where(entry => entry.Enabled
                            && (options.AllowedEntryIds is null || options.AllowedEntryIds.Contains(entry.Id))
                            && (options.Purpose == SearchPurpose.Automatic ? entry.Settings.AutomaticSearch : entry.Settings.InteractiveSearch))
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entries.Length == 0)
        {
            return AcquisitionSearchResult.Empty;
        }

        var session = new SearchSession(options);
        using var gate = new SemaphoreSlim(budget.MaxConcurrentIndexers);
        var runs = await Task.WhenAll(entries.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await RunIndexerAsync(entry, kind, planFor, options, budget, session, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));

        var hits = runs.SelectMany(run => run.Hits).ToArray();
        return new AcquisitionSearchResult(
            ReleaseDeduplicator.Merge(hits),
            [.. runs.Select(run => run.Outcome)],
            [.. runs.SelectMany(run => run.Trace)],
            hits.Length);
    }

    private async Task<IndexerRun> RunIndexerAsync(
        IndexerEntry entry,
        MediaAcquisitionKind kind,
        Func<IndexerCapabilities?, IReadOnlyList<PlannedQuery>> planFor,
        SearchOptions options,
        SearchBudget budget,
        SearchSession session,
        CancellationToken cancellationToken)
    {
        if (entry.Settings.MediaKinds is { Length: > 0 } kinds && !kinds.Contains(kind))
        {
            return IndexerRun.Skipped(entry, $"Not searched for {kind}.");
        }

        if (!await health.IsHealthyAsync(AcquisitionHealthKind.Indexer, entry.Id, cancellationToken))
        {
            var reason = (await health.GetAsync(AcquisitionHealthKind.Indexer, entry.Id, cancellationToken))?.LastError ?? "unhealthy";
            logger.LogWarning("Skipped indexer '{Indexer}': {Reason}", entry.Name, reason);
            return IndexerRun.Skipped(entry, $"Skipped: {reason}");
        }

        if (cache.IsBackedOff(entry.Id, out var until, out var backoffReason))
        {
            return IndexerRun.Failed(entry, IndexerSearchState.RateLimited, $"Waiting until {until:HH:mm} UTC: {backoffReason}", until);
        }

        if (!indexers.TryGetValue(entry.Type, out var indexer))
        {
            return IndexerRun.Skipped(entry, "No client is registered for this indexer type.");
        }

        var capabilities = entry.Type == IndexerType.Newznab ? entry.Settings.Capabilities : null;
        var plan = planFor(capabilities);
        var limit = Math.Min(entry.Settings.SearchLimit, capabilities?.MaximumLimit ?? int.MaxValue);
        var effective = entry.Type == IndexerType.Prowlarr && options.ProwlarrIndexerIds is { Count: > 0 } prowlarrIds
            ? entry with { Settings = entry.Settings with { IndexerIds = [.. prowlarrIds] } }
            : entry;
        var categories = SearchPlanner.Categories(kind, entry).ToArray();

        var hits = new List<SearchHit>();
        var trace = new List<SearchTraceLine>();
        var queriesRun = 0;
        string? failure = null;
        var state = IndexerSearchState.Searched;
        DateTimeOffset? retryAfter = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var allowed = options.IndexerTimeout is { } shorter && shorter < budget.IndexerTimeout ? shorter : budget.IndexerTimeout;
        timeout.CancelAfter(allowed);
        try
        {
            for (var tier = 0; tier <= budget.MaxTier; tier++)
            {
                if (tier > 0 && (session.UsableCount() >= budget.TargetUsable || session.RawCount >= budget.MaxResults))
                {
                    break;
                }

                foreach (var query in plan.Where(candidate => candidate.Tier == tier))
                {
                    queriesRun++;
                    await ReadQueryPagesAsync(indexer, effective, entry, query, categories, limit, options, budget, session, hits, trace, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            state = IndexerSearchState.TimedOut;
            failure = $"No answer within {allowed.TotalSeconds:0.##} s.";
        }
        catch (IndexerRateLimitedException exception)
        {
            state = IndexerSearchState.RateLimited;
            retryAfter = cache.BackOff(entry.Id, exception.RetryAfter, exception.Message);
            failure = exception.Message;
        }
        catch (IndexerAuthenticationException exception)
        {
            state = IndexerSearchState.AuthenticationFailed;
            failure = exception.Message;
        }
        catch (Exception exception) when (exception is IndexerException or ProwlarrException or HttpRequestException)
        {
            state = IndexerSearchState.Unavailable;
            failure = exception.Message;
        }

        // An indexer that answered some queries before it failed keeps what it returned and reports the problem next to it.
        if (state != IndexerSearchState.Searched && hits.Count == 0)
        {
            return IndexerRun.Failed(entry, state, failure ?? state.ToString(), retryAfter) with { Trace = trace };
        }

        var outcome = new IndexerSearchOutcome(
            entry.Id,
            entry.Name,
            hits.Count == 0 ? IndexerSearchState.NoResults : IndexerSearchState.Searched,
            queriesRun,
            hits.Count,
            failure,
            retryAfter);
        return new IndexerRun(outcome, hits, trace);
    }

    private async Task ReadQueryPagesAsync(
        IIndexer indexer,
        IndexerEntry effective,
        IndexerEntry original,
        PlannedQuery query,
        int[] categories,
        int limit,
        SearchOptions options,
        SearchBudget budget,
        SearchSession session,
        List<SearchHit> hits,
        List<SearchTraceLine> trace,
        CancellationToken cancellationToken)
    {
        var entry = effective with { Settings = effective.Settings with { Categories = query.AnyCategory ? [] : categories } };
        for (var page = 0; page < budget.MaxPages; page++)
        {
            var key = $"{original.Id:N}|{query.Key}|{string.Join(',', entry.Settings.Categories)}|{limit}|{page}";
            // Only a person browsing candidates reads the evidence cache. Wanted searches run hours apart and must see what appeared since
            // the last one, so an automatic search always asks the indexer and never hides new content behind a remembered answer.
            var useCache = options.Purpose == SearchPurpose.Interactive && !options.Refresh;
            IReadOnlyList<ProwlarrReleaseCandidate> results;
            var cached = false;
            if (useCache && cache.TryGet(key, out var remembered))
            {
                results = remembered;
                cached = true;
            }
            else
            {
                results = await indexer.SearchAsync(entry, new IndexerSearchQuery(query.Text ?? string.Empty, query.Mode, query.Parameters, page * limit, limit), cancellationToken);
                if (options.Purpose == SearchPurpose.Interactive)
                {
                    cache.Set(key, results);
                }
            }

            var fresh = 0;
            foreach (var release in results)
            {
                hits.Add(new SearchHit(release, original.Id, original.Priority, original.Name, query));
                if (session.Add(release))
                {
                    fresh++;
                }
            }

            trace.Add(new SearchTraceLine(original.Name, query.Stage, query.Provenance, query.Text ?? string.Join(' ', query.Parameters.Select(pair => $"{pair.Key}={pair.Value}")), page, results.Count, fresh, cached));

            // The next page is only worth reading when this one was full, still brought something new and a bound allows it.
            var wantsMore = options.Depth == SearchDepth.Deep || session.UsableCount() < budget.TargetUsable;
            if (results.Count < limit || fresh == 0 || !wantsMore || session.RawCount >= budget.MaxResults)
            {
                return;
            }
        }
    }

    private sealed record IndexerRun(IndexerSearchOutcome Outcome, List<SearchHit> Hits, List<SearchTraceLine> Trace)
    {
        public static IndexerRun Skipped(IndexerEntry entry, string message) =>
            new(new IndexerSearchOutcome(entry.Id, entry.Name, IndexerSearchState.Skipped, 0, 0, message), [], []);

        public static IndexerRun Failed(IndexerEntry entry, IndexerSearchState state, string message, DateTimeOffset? retryAfter = null) =>
            new(new IndexerSearchOutcome(entry.Id, entry.Name, state, 0, 0, message, retryAfter), [], []);
    }

    /// <summary>The running view of one search that all indexers share: how many distinct candidates exist and how many are usable, for the stop conditions.</summary>
    private sealed class SearchSession(SearchOptions options)
    {
        private readonly object gate = new();
        private readonly Dictionary<string, ProwlarrReleaseCandidate> distinct = new(StringComparer.Ordinal);
        private int raw;

        public int RawCount
        {
            get
            {
                lock (gate)
                {
                    return raw;
                }
            }
        }

        public bool Add(ProwlarrReleaseCandidate release)
        {
            lock (gate)
            {
                raw++;
                return distinct.TryAdd(ReleaseDeduplicator.ProvisionalKey(release), release);
            }
        }

        public int UsableCount()
        {
            ProwlarrReleaseCandidate[] snapshot;
            lock (gate)
            {
                snapshot = [.. distinct.Values];
            }

            return options.UsableCount is { } count ? count(snapshot) : snapshot.Length;
        }
    }
}
