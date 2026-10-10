using Jularr.Web.Features.Playback;

namespace Jularr.Web.Features.Search;

/// <summary>
/// One canonical work among the candidates: every source record that bridges to the same media-core
/// <c>Work</c> (or a single unbridged record), best variant first.
/// </summary>
public sealed record MediaSearchGroup(long? WorkId, IReadOnlyList<MediaSearchVariant> Variants)
{
    public MediaSearchVariant Best => Variants[0];
}

/// <summary>
/// The pure part of the search: collapsing candidates into canonical works, aggregating their Media
/// Facts, applying the Media Facts filters and ordering. It has no database access so the rules are
/// testable on their own.
/// </summary>
public static class MediaSearchGrouping
{
    /// <summary>
    /// Collapses variants that bridge to the same work. A variant without a bridge is a group of one:
    /// the same record always resolves to itself, never to another type's record by title.
    /// </summary>
    public static IReadOnlyList<MediaSearchGroup> Group(
        IEnumerable<MediaSearchVariant> variants,
        IReadOnlyDictionary<(MediaSearchType Type, Guid Id), long> workOf)
    {
        var groups = new List<MediaSearchGroup>();
        var byWork = new Dictionary<long, List<MediaSearchVariant>>();

        foreach (var variant in variants)
        {
            if (workOf.TryGetValue((variant.Type, variant.Id), out var workId))
            {
                if (!byWork.TryGetValue(workId, out var members))
                {
                    byWork[workId] = members = [];
                }

                members.Add(variant);
            }
            else
            {
                groups.Add(new MediaSearchGroup(null, [variant]));
            }
        }

        groups.AddRange(byWork.Select(pair => new MediaSearchGroup(pair.Key, pair.Value)));

        return
        [
            .. groups.Select(group => group with { Variants = [.. group.Variants.OrderBy(v => v, VariantOrder)] })
        ];
    }

    /// <summary>Best score first; equal scores prefer the type listed first, then title, then id.</summary>
    public static IComparer<MediaSearchVariant> VariantOrder { get; } = Comparer<MediaSearchVariant>.Create(
        (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byType = left.Type.CompareTo(right.Type);
            if (byType != 0)
            {
                return byType;
            }

            var byTitle = string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
            return byTitle != 0 ? byTitle : left.Id.CompareTo(right.Id);
        });

    /// <summary>
    /// The facts of a canonical work from those of its variants: local when any variant is, monitored
    /// and wanted when any is, languages and genres the union, the earliest year any of them knows.
    /// </summary>
    public static MediaSearchFacts Aggregate(IEnumerable<MediaSearchFacts> facts, int? workYear)
    {
        var all = facts.ToArray();
        return new MediaSearchFacts(
            all.Select(x => x.Year).Append(workYear).Where(year => year is > 0).Min(),
            [.. all.SelectMany(x => x.Languages).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. all.SelectMany(x => x.Genres).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            all.Any(x => x.HasLocalContent),
            all.Any(x => x.Monitored),
            all.Any(x => x.Wanted));
    }

    /// <summary>
    /// Whether a title passes the Media Facts filters. A filter on a fact never matches a title whose
    /// fact is unknown (no year, no language, no genre): search keeps what it can prove.
    /// </summary>
    public static bool Matches(MediaSearchFacts facts, MediaSearchFilters filters)
    {
        if (filters.Local is { } local && facts.HasLocalContent != local)
        {
            return false;
        }

        if (filters.Monitored is { } monitored && facts.Monitored != monitored)
        {
            return false;
        }

        if (filters.Wanted is { } wanted && facts.Wanted != wanted)
        {
            return false;
        }

        if (NormalizeLanguage(filters.Language) is { } language && !facts.Languages.Contains(language))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filters.Genre)
            && !facts.Genres.Contains(filters.Genre.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filters.YearFrom is not null || filters.YearTo is not null)
        {
            if (facts.Year is not { } year
                || (filters.YearFrom is { } from && year < from)
                || (filters.YearTo is { } to && year > to))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The row order of the result list: best score first, then a franchise before the works it groups,
    /// then title and id so paging is stable.
    /// </summary>
    public static IEnumerable<MediaSearchResult> Order(IEnumerable<MediaSearchResult> results) =>
        results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Kind == MediaSearchResultKind.Franchise ? 0 : 1)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Id);

    public static MediaSearchFacets BuildFacets(IReadOnlyCollection<MediaSearchFacts> facts)
    {
        var years = facts.Select(x => x.Year).Where(year => year is > 0).Select(year => year!.Value).ToArray();
        return new MediaSearchFacets(
            Ranked(facts.SelectMany(x => x.Languages), StringComparer.Ordinal, 40),
            Ranked(facts.SelectMany(x => x.Genres), StringComparer.OrdinalIgnoreCase, 60),
            years.Length == 0 ? null : years.Min(),
            years.Length == 0 ? null : years.Max());
    }

    /// <summary>The canonical form of a language filter, so <c>jpn</c> and <c>ja</c> select the same titles.</summary>
    public static string? NormalizeLanguage(string? language) => PlaybackLanguages.Normalize(language);

    private static IReadOnlyList<string> Ranked(IEnumerable<string> values, StringComparer comparer, int max) =>
    [
        .. values
            .GroupBy(value => value, comparer)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, comparer)
            .Take(max)
            .Select(group => group.Key)
    ];
}
