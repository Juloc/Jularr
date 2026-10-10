using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Core;

namespace Jularr.Web.Features.Acquisition.Search;

/// <summary>One indexer's answer for a release together with the query that produced it.</summary>
public sealed record SearchHit(AcquisitionCandidate Release, Guid EntryId, int Priority, string IndexerName, PlannedQuery Query);

/// <summary>
/// Cross-indexer deduplication: equivalent releases returned by several indexers become one logical candidate that keeps every
/// source and query. Two releases are only the same when independent facts agree (the same indexer's GUID, or title, size, release
/// group and posting time); a title alone is never enough, so a re-post of a different size or a different group's release of the
/// same name stays a separate candidate.
/// </summary>
public static class ReleaseDeduplicator
{
    private const double SizeTolerance = 0.01;
    private static readonly TimeSpan PostedTolerance = TimeSpan.FromHours(72);

    /// <summary>The cheap key a running search uses to count distinct candidates; the final clustering uses <see cref="SameRelease"/>.</summary>
    public static string ProvisionalKey(AcquisitionCandidate release) =>
        $"{NormalizedTitle(release.Title)}|{release.SizeBytes?.ToString() ?? "?"}";

    public static IReadOnlyList<AcquisitionCandidate> Merge(IEnumerable<SearchHit> hits)
    {
        // A fixed order (indexer priority, then name, then identity) makes the primary source and the grouping independent of which
        // indexer answered first.
        var ordered = hits
            .OrderBy(hit => hit.Priority)
            .ThenBy(hit => hit.IndexerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.Release.Identity, StringComparer.Ordinal)
            .ThenBy(hit => hit.Query.Key, StringComparer.Ordinal)
            .ToArray();

        var clusters = new List<List<SearchHit>>();
        var byTitle = new Dictionary<string, List<List<SearchHit>>>(StringComparer.Ordinal);
        foreach (var hit in ordered)
        {
            var title = NormalizedTitle(hit.Release.Title);
            if (!byTitle.TryGetValue(title, out var candidates))
            {
                candidates = [];
                byTitle[title] = candidates;
            }

            var home = candidates.FirstOrDefault(cluster => cluster.Any(member => SameRelease(member, hit)));
            if (home is null)
            {
                home = [];
                candidates.Add(home);
                clusters.Add(home);
            }

            home.Add(hit);
        }

        return [.. clusters.Select(Build)
            .OrderByDescending(release => release.PublishedAt)
            .ThenBy(release => release.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(release => release.Identity, StringComparer.Ordinal)];
    }

    public static bool SameRelease(SearchHit left, SearchHit right)
    {
        if (left.EntryId == right.EntryId)
        {
            return string.Equals(left.Release.Identity, right.Release.Identity, StringComparison.Ordinal);
        }

        var a = left.Release;
        var b = right.Release;
        if (a.SizeBytes is not { } sizeA || b.SizeBytes is not { } sizeB || sizeA <= 0 || sizeB <= 0)
        {
            return false;
        }

        if (Math.Abs(sizeA - sizeB) > Math.Max(sizeA, sizeB) * SizeTolerance
            || !string.Equals(NormalizedTitle(a.Title), NormalizedTitle(b.Title), StringComparison.Ordinal)
            || !string.Equals(a.ParsedRelease.ReleaseGroup, b.ParsedRelease.ReleaseGroup, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return a.PublishedAt is not { } postedA || b.PublishedAt is not { } postedB || (postedA - postedB).Duration() <= PostedTolerance;
    }

    private static AcquisitionCandidate Build(List<SearchHit> cluster)
    {
        var primary = cluster[0].Release;
        var sources = cluster
            .GroupBy(hit => hit.Release.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(hit => new ReleaseSourceOption(hit.IndexerName, hit.EntryId, hit.Priority, hit.Release.Guid, hit.Release.InternalDownloadUri, hit.Release.InfoUrl, hit.Release.Identity))
            .ToArray();
        var provenance = cluster
            .Select(hit => new QueryProvenance(hit.IndexerName, hit.Query.Stage, hit.Query.Provenance, hit.Query.Text ?? string.Join(' ', hit.Query.Parameters.Select(pair => $"{pair.Key}={pair.Value}"))))
            .Distinct()
            .ToArray();
        var queries = cluster.SelectMany(hit => hit.Release.MatchedQueries).Concat(provenance.Select(entry => entry.QueryText))
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return primary with { Sources = sources, Provenance = provenance, MatchedQueries = queries };
    }

    private static string NormalizedTitle(string title) =>
        new([.. title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
