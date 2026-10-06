using Jularr.Web.Features.Acquisition.Prowlarr;

namespace Jularr.Web.Features.Acquisition.Indexers;

/// <summary>
/// Adapts the existing, unchanged Prowlarr client/parsing
/// (<see cref="IProwlarrClient"/>) to the canonical <see cref="IIndexer"/>
/// abstraction, so Prowlarr is one indexer among several instead of a
/// hardcoded special case.
/// </summary>
public sealed class ProwlarrIndexer(IProwlarrClient client) : IIndexer
{
    public IndexerType Type => IndexerType.Prowlarr;

    public async Task<IndexerConnectionTestResult> TestAsync(
        IndexerEntry entry,
        CancellationToken cancellationToken)
    {
        var result = await client.TestAsync(ToConnection(entry), cancellationToken);
        return new IndexerConnectionTestResult(result.Success, result.Version, result.Error);
    }

    public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
        IndexerEntry entry,
        IndexerSearchQuery query,
        CancellationToken cancellationToken) =>
        client.SearchAsync(ToConnection(entry), new ProwlarrSearchQuery(query.Query, query.Offset, query.Limit), cancellationToken);

    private static ProwlarrConnection ToConnection(IndexerEntry entry) =>
        new(
            new ProwlarrSettings(
                entry.Settings.BaseUrl,
                entry.Settings.Categories,
                entry.Settings.IndexerIds,
                entry.Settings.SearchLimit),
            entry.ApiKey);
}
