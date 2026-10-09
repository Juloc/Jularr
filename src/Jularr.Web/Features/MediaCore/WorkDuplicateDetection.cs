namespace Jularr.Web.Features.MediaCore;

/// <summary>A flattened title of a work, used only for duplicate detection (never display).</summary>
public sealed record WorkTitleProbe(long WorkId, WorkMediaType MediaType, int? Year, string NormalizedValue);

/// <summary>
/// One suggested pair of works that look like the same real work (#432 / #437). The pair is ordered so
/// <see cref="KeepWorkId"/> is the recommended survivor of a merge (more evidence / earlier id) and
/// <see cref="MergeWorkId"/> the one to absorb. <see cref="Score"/> ranks stronger matches first.
/// </summary>
public sealed record WorkDuplicateSuggestion(
    long KeepWorkId,
    string KeepTitle,
    long MergeWorkId,
    string MergeTitle,
    WorkMediaType MediaType,
    double Score,
    IReadOnlyList<string> Evidence);

/// <summary>
/// Pure, deterministic duplicate detection over the universal media core (#432). Two works are a
/// duplicate candidate when — within the <b>same</b> media type — they share at least one normalized
/// title. A shared release year lifts the score; a title shared across media types is deliberately
/// <i>not</i> a duplicate (that is an adaptation relation, not the same work). External provider
/// identities cannot themselves collide across works (the unique index forbids it), so a shared
/// identity never appears here; title evidence is the durable signal this layer can compute.
/// </summary>
public static class WorkDuplicateDetection
{
    private const double SharedTitleScore = 0.6;
    private const double SameYearBonus = 0.3;
    private const double MultipleTitlesBonus = 0.1;

    /// <summary>
    /// Builds ranked merge suggestions from every work title. <paramref name="relatedPairs"/> lists work
    /// pairs that already carry a relation edge (either direction, keyed unordered) — the owner or a
    /// provider has already resolved those, so they are suppressed from the suggestions.
    /// </summary>
    public static IReadOnlyList<WorkDuplicateSuggestion> Suggest(
        IReadOnlyDictionary<long, string> titlesByWork,
        IReadOnlyList<WorkTitleProbe> probes,
        IReadOnlySet<(long, long)> relatedPairs)
    {
        var pairs = new Dictionary<(long, long), PairEvidence>();

        var byKey = probes
            .Where(p => p.NormalizedValue.Length > 0)
            .GroupBy(p => (p.MediaType, p.NormalizedValue));

        foreach (var group in byKey)
        {
            var works = group
                .GroupBy(p => p.WorkId)
                .Select(g => g.First())
                .OrderBy(p => p.WorkId)
                .ToArray();
            if (works.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < works.Length; i++)
            {
                for (var j = i + 1; j < works.Length; j++)
                {
                    var a = works[i];
                    var b = works[j];
                    var key = Key(a.WorkId, b.WorkId);
                    if (relatedPairs.Contains(key))
                    {
                        continue;
                    }

                    if (!pairs.TryGetValue(key, out var evidence))
                    {
                        evidence = new PairEvidence(a.MediaType, a.Year, b.Year);
                        pairs[key] = evidence;
                    }

                    evidence.SharedTitles.Add(group.Key.NormalizedValue);
                }
            }
        }

        var suggestions = new List<WorkDuplicateSuggestion>();
        foreach (var ((first, second), evidence) in pairs)
        {
            var score = SharedTitleScore
                + (evidence.SharedTitles.Count > 1 ? MultipleTitlesBonus : 0)
                + (evidence.YearA is { } ya && evidence.YearB is { } yb && ya == yb ? SameYearBonus : 0);

            var reasons = new List<string>
            {
                $"shared title: {string.Join(", ", evidence.SharedTitles.OrderBy(x => x, StringComparer.Ordinal))}"
            };
            if (evidence.YearA is { } y1 && evidence.YearB is { } y2 && y1 == y2)
            {
                reasons.Add($"same year: {y1}");
            }

            // Recommend the lower-id work as the survivor for a stable, deterministic default.
            suggestions.Add(new WorkDuplicateSuggestion(
                first,
                titlesByWork.GetValueOrDefault(first, ""),
                second,
                titlesByWork.GetValueOrDefault(second, ""),
                evidence.MediaType,
                Math.Round(Math.Min(score, 1.0), 3),
                reasons));
        }

        return suggestions
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.KeepTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Unordered key for a work pair, so (a,b) and (b,a) collapse to one suggestion.</summary>
    public static (long, long) Key(long a, long b) =>
        a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    private sealed class PairEvidence(WorkMediaType mediaType, int? yearA, int? yearB)
    {
        public WorkMediaType MediaType { get; } = mediaType;
        public int? YearA { get; } = yearA;
        public int? YearB { get; } = yearB;
        public HashSet<string> SharedTitles { get; } = new(StringComparer.Ordinal);
    }
}
