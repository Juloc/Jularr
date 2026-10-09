using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Health;

namespace Jularr.Web.Features.Acquisition.Indexers;

public enum IndexerSetupOutcome
{
    /// <summary>The indexer was added; <see cref="IndexerSetupResult.Entry"/> holds what was detected.</summary>
    Added,

    /// <summary>The capabilities were read again and the searches were checked again.</summary>
    Refreshed,

    /// <summary>The searches were checked again with the capabilities already stored.</summary>
    Checked,

    /// <summary>The Base URL is not a usable address; <see cref="IndexerSetupResult.Detail"/> says why.</summary>
    InvalidAddress,

    /// <summary>An indexer with this address already exists.</summary>
    Duplicate,

    /// <summary>The indexer could not be read (connection, key or API); an existing entry is left exactly as it was.</summary>
    ConnectionFailed,

    NotFound
}

/// <param name="State">Why a failed connection failed, or <see cref="IndexerCheckState.Valid"/> when it worked.</param>
/// <param name="Detail">The indexer-side explanation in English, without the API key; null when there is nothing to add.</param>
public sealed record IndexerSetupResult(IndexerSetupOutcome Outcome, IndexerEntry? Entry, IndexerCheckState State = IndexerCheckState.Valid, string? Detail = null);

/// <summary>
/// The one setup path of a direct Newznab indexer: the address and the API key are all the owner supplies. Adding tests the connection and key,
/// reads the caps, proves with bounded real requests that the search functions answer, and stores what was found; refreshing does the same for
/// an existing entry and never touches the owner's settings, nor replaces a working configuration when the indexer is only temporarily away.
/// No request here downloads anything: probes ask for one release of the newest feed.
/// </summary>
public sealed class IndexerSetupService(IndexerStore store, IReadOnlyDictionary<IndexerType, IIndexer> indexers, AcquisitionHealthStore health, TimeProvider? clock = null)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly IndexerSearchMode[] Functions = [IndexerSearchMode.Search, IndexerSearchMode.TvSearch, IndexerSearchMode.Movie, IndexerSearchMode.Book, IndexerSearchMode.Music];
    private const int MaximumCategoryProbes = 8;

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>The address as it is stored: a scheme (https unless given), no credentials, query or fragment, and no trailing <c>/api</c>; a custom path is kept.</summary>
    public static bool TryNormalizeBaseUrl(string? raw, out string baseUrl, out string? problem)
    {
        baseUrl = string.Empty;
        problem = null;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            problem = "Enter the indexer's address.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
        {
            problem = "The address is not a valid http(s) address.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            problem = "Remove the user name and password from the address.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            problem = "Remove everything after the question mark. The API key has its own field.";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        baseUrl = $"{uri.Scheme}://{uri.Authority}{path}";
        return true;
    }

    /// <summary>Adds a Newznab indexer from its address and API key; the name is optional and defaults to what the indexer reports about itself.</summary>
    public async Task<IndexerSetupResult> AddAsync(string? address, string? apiKey, string? name, CancellationToken cancellationToken)
    {
        if (!TryNormalizeBaseUrl(address, out var baseUrl, out var problem))
        {
            return new IndexerSetupResult(IndexerSetupOutcome.InvalidAddress, null, IndexerCheckState.InvalidResponse, problem);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new IndexerSetupResult(IndexerSetupOutcome.ConnectionFailed, null, IndexerCheckState.AuthenticationFailed, "Enter the API key.");
        }

        var existing = await store.LoadAllAsync(cancellationToken);
        if (existing.FirstOrDefault(entry => entry.Type == IndexerType.Newznab && string.Equals(entry.Settings.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)) is { } duplicate)
        {
            return new IndexerSetupResult(IndexerSetupOutcome.Duplicate, duplicate);
        }

        var entry = new IndexerEntry(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(name) ? new Uri(baseUrl).Host : name.Trim(),
            IndexerType.Newznab,
            Enabled: true,
            existing.Count == 0 ? 1 : existing.Max(item => item.Priority) + 1,
            IndexerSettings.CreateDefault(baseUrl, IndexerType.Newznab),
            apiKey.Trim());
        var indexer = indexers[IndexerType.Newznab];
        var connection = await indexer.TestAsync(entry, cancellationToken);
        if (connection.State == IndexerCheckState.Unavailable && !(address ?? string.Empty).Contains("://", StringComparison.Ordinal))
        {
            // An address typed without a scheme is tried as https first; a self-hosted indexer on the local network often only speaks http.
            var plain = entry with { Settings = entry.Settings with { BaseUrl = "http" + baseUrl[5..] } };
            var retry = await indexer.TestAsync(plain, cancellationToken);
            if (retry.Success)
            {
                (entry, connection, baseUrl) = (plain, retry, plain.Settings.BaseUrl);
            }
        }

        if (!connection.Success || connection.Capabilities is not { } capabilities)
        {
            return new IndexerSetupResult(IndexerSetupOutcome.ConnectionFailed, null, connection.State, connection.Error);
        }

        if (string.IsNullOrWhiteSpace(name) && capabilities.ServerTitle is { } title)
        {
            entry = entry with { Name = title };
        }

        var withCaps = entry with { Settings = entry.Settings with { Capabilities = capabilities } };
        var searches = await ProbeFunctionsAsync(indexer, withCaps, capabilities, cancellationToken);
        var verification = new IndexerVerification(time.GetUtcNow(), Connected: true, Authenticated: true, searches);

        // An indexer whose plain search was not proven stays off until the owner has seen why and refreshed it.
        var saved = withCaps with { Enabled = verification.Answered(IndexerSearchMode.Search), Settings = withCaps.Settings with { Verification = verification } };
        await store.SaveAsync(saved, cancellationToken);
        await RecordHealthAsync(saved, true, null, cancellationToken);
        return new IndexerSetupResult(IndexerSetupOutcome.Added, saved, searches[IndexerSearchMode.Search].State, searches[IndexerSearchMode.Search].Message);
    }

    /// <summary>Reads the caps again and re-checks the search functions. When the indexer cannot be read now, the stored configuration stays and the failure is recorded beside it.</summary>
    public async Task<IndexerSetupResult> RefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(id, cancellationToken) is not { Type: IndexerType.Newznab } entry)
        {
            return new IndexerSetupResult(IndexerSetupOutcome.NotFound, null);
        }

        var indexer = indexers[IndexerType.Newznab];
        var connection = await indexer.TestAsync(entry, cancellationToken);
        var now = time.GetUtcNow();
        if (!connection.Success || connection.Capabilities is not { } capabilities)
        {
            var previous = entry.Settings.Verification;
            var failed = (previous ?? new IndexerVerification(now, Connected: connection.State != IndexerCheckState.Unavailable, Authenticated: connection.State != IndexerCheckState.AuthenticationFailed, new Dictionary<IndexerSearchMode, IndexerCheck>()))
                with { RefreshError = connection.Error, RefreshFailedAt = now };
            await store.UpdateDiscoveryAsync(id, null, failed, cancellationToken);
            await RecordHealthAsync(entry, false, connection.Error, cancellationToken);
            return new IndexerSetupResult(IndexerSetupOutcome.ConnectionFailed, entry, connection.State, connection.Error);
        }

        var searches = await ProbeFunctionsAsync(indexer, entry with { Settings = entry.Settings with { Capabilities = capabilities } }, capabilities, cancellationToken);
        await store.UpdateDiscoveryAsync(id, capabilities, new IndexerVerification(now, true, true, searches), cancellationToken);
        await RecordHealthAsync(entry, true, null, cancellationToken);
        return new IndexerSetupResult(IndexerSetupOutcome.Refreshed, await store.GetAsync(id, cancellationToken), searches[IndexerSearchMode.Search].State, searches[IndexerSearchMode.Search].Message);
    }

    /// <summary>Runs the bounded validation searches again with the stored capabilities, now also once per distinct category set the media types resolve to.</summary>
    public async Task<IndexerSetupResult> TestSearchAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(id, cancellationToken) is not { Type: IndexerType.Newznab } entry)
        {
            return new IndexerSetupResult(IndexerSetupOutcome.NotFound, null);
        }

        var capabilities = entry.Settings.Capabilities ?? IndexerCapabilities.TextOnly(time.GetUtcNow());
        var indexer = indexers[IndexerType.Newznab];
        var searches = await ProbeFunctionsAsync(indexer, entry, capabilities, cancellationToken);
        var kinds = new Dictionary<MediaAcquisitionKind, IndexerCheck>();
        if (searches[IndexerSearchMode.Search].State is IndexerCheckState.Valid or IndexerCheckState.NoResults)
        {
            var groups = Enum.GetValues<MediaAcquisitionKind>()
                .Select(kind => IndexerCategoryMapper.Resolve(kind, entry))
                .Where(item => item.IsAvailable)
                .GroupBy(item => string.Join(',', item.Categories))
                .Take(MaximumCategoryProbes);
            foreach (var group in groups)
            {
                var check = await ProbeAsync(indexer, entry with { Settings = entry.Settings with { Categories = group.First().Categories } }, new IndexerSearchQuery(string.Empty, IndexerSearchMode.Search, null, 0, 1, Latest: true), cancellationToken);
                foreach (var item in group)
                {
                    kinds[item.Kind] = check;
                }

                if (check.State is IndexerCheckState.AuthenticationFailed or IndexerCheckState.RateLimited or IndexerCheckState.Unavailable)
                {
                    break;
                }
            }
        }

        await store.UpdateDiscoveryAsync(id, null, new IndexerVerification(time.GetUtcNow(), true, true, searches, null, null, kinds), cancellationToken);
        return new IndexerSetupResult(IndexerSetupOutcome.Checked, await store.GetAsync(id, cancellationToken), searches[IndexerSearchMode.Search].State, searches[IndexerSearchMode.Search].Message);
    }

    /// <summary>One request per advertised search function (at most five), newest feed only; the first authentication, limit or availability problem ends the run.</summary>
    private async Task<IReadOnlyDictionary<IndexerSearchMode, IndexerCheck>> ProbeFunctionsAsync(IIndexer indexer, IndexerEntry entry, IndexerCapabilities capabilities, CancellationToken cancellationToken)
    {
        var probe = entry with { Settings = entry.Settings with { Categories = [] } };
        var checks = new Dictionary<IndexerSearchMode, IndexerCheck>();
        var stopped = false;
        foreach (var mode in Functions.Where(capabilities.Supports))
        {
            if (stopped)
            {
                checks[mode] = new IndexerCheck(IndexerCheckState.NotChecked);
                continue;
            }

            var check = await ProbeAsync(indexer, probe, new IndexerSearchQuery(string.Empty, mode, null, 0, 1, Latest: true), cancellationToken);
            if (mode == IndexerSearchMode.Search && check.State == IndexerCheckState.ParametersRejected)
            {
                // A search that insists on text is still a working search: ask once with a word instead of the bare feed.
                check = await ProbeAsync(indexer, probe, new IndexerSearchQuery("test", mode, null, 0, 1), cancellationToken);
            }

            checks[mode] = check;
            stopped = check.State is IndexerCheckState.AuthenticationFailed or IndexerCheckState.RateLimited or IndexerCheckState.Unavailable;
        }

        if (!checks.ContainsKey(IndexerSearchMode.Search))
        {
            checks[IndexerSearchMode.Search] = new IndexerCheck(IndexerCheckState.NotChecked);
        }

        return checks;
    }

    private static async Task<IndexerCheck> ProbeAsync(IIndexer indexer, IndexerEntry entry, IndexerSearchQuery query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var results = await indexer.SearchAsync(entry, query, timeout.Token);
            return new IndexerCheck(results.Count > 0 ? IndexerCheckState.Valid : IndexerCheckState.NoResults);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new IndexerCheck(IndexerCheckState.Unavailable, "No answer within 20 s.");
        }
        catch (IndexerAuthenticationException exception)
        {
            return new IndexerCheck(IndexerCheckState.AuthenticationFailed, exception.Message);
        }
        catch (IndexerRateLimitedException exception)
        {
            return new IndexerCheck(IndexerCheckState.RateLimited, exception.Message);
        }
        catch (IndexerRequestRejectedException exception)
        {
            return new IndexerCheck(IndexerCheckState.ParametersRejected, exception.Message);
        }
        catch (IndexerException exception)
        {
            return new IndexerCheck(IndexerCheckState.InvalidResponse, exception.Message);
        }
        catch (HttpRequestException)
        {
            return new IndexerCheck(IndexerCheckState.Unavailable, "The indexer could not be reached.");
        }
    }

    private Task RecordHealthAsync(IndexerEntry entry, bool healthy, string? error, CancellationToken cancellationToken) =>
        health.RecordAsync(new AcquisitionHealthStatus(AcquisitionHealthKind.Indexer, entry.Id, entry.Name, healthy, healthy, error, time.GetUtcNow()), cancellationToken);
}
