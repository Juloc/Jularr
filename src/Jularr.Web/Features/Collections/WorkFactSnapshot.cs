using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaFacts;
using Jularr.Web.Ui;
using MediaFactsProjection = Jularr.Web.Features.MediaFacts.MediaFacts;

namespace Jularr.Web.Features.Collections;

/// <summary>
/// One language observed for a work, flattened for rule evaluation: its BCP-47 code and whether it covers
/// every known unit of the work (complete) or only some (partial). Built from <see cref="MediaFactsLanguageRow"/>.
/// </summary>
public sealed record WorkFactLanguage(string Code, bool IsComplete);

/// <summary>
/// The immutable, provider-independent fact snapshot a smart-collection rule is evaluated against (#427).
/// It gathers everything a rule can test for one <see cref="Work"/> — media type, release status, year,
/// unit counts, language coverage and franchise membership — into a single value with no database or
/// provider dependency, so the evaluator is pure and fully unit-testable. Cards are built from the same
/// snapshot, so what a rule matched and what the shelf shows can never disagree.
/// </summary>
public sealed record WorkFactSnapshot(
    long WorkId,
    WorkMediaType MediaType,
    string Title,
    int? Year,
    MediaReleaseStatus? Status,
    int? PrimaryUnitCount,
    int? SecondaryUnitCount,
    int? RuntimeMinutes,
    IReadOnlyList<WorkFactLanguage> Languages,
    IReadOnlyList<Guid> FranchiseIds)
{
    public bool IsInFranchise => FranchiseIds.Count > 0;

    /// <summary>Every language code present at any coverage, upper-cased for display and comparison.</summary>
    public IEnumerable<string> LanguageCodes => Languages.Select(x => x.Code);

    /// <summary>Language codes that cover every known unit of the work.</summary>
    public IEnumerable<string> CompleteLanguageCodes => Languages.Where(x => x.IsComplete).Select(x => x.Code);

    /// <summary>The equivalent <see cref="MediaBannerKind"/> for card rendering; null for types without a banner kind.</summary>
    public MediaBannerKind? BannerKind => MediaType switch
    {
        WorkMediaType.Anime => MediaBannerKind.Anime,
        WorkMediaType.Manga => MediaBannerKind.Manga,
        WorkMediaType.LightNovel => MediaBannerKind.LightNovel,
        WorkMediaType.Book => MediaBannerKind.Book,
        _ => null
    };

    /// <summary>Flattens a media-facts projection into the language facts a rule tests.</summary>
    public static IReadOnlyList<WorkFactLanguage> LanguagesFrom(MediaFactsProjection facts) =>
    [
        .. facts.Languages
            .Select(row => new WorkFactLanguage(
                row.Language.ToLowerInvariant(),
                row.Coverage == MediaFactsCoverage.Complete))
            // A language can appear as both audio and subtitle; collapse to the strongest coverage.
            .GroupBy(x => x.Code)
            .Select(group => new WorkFactLanguage(group.Key, group.Any(x => x.IsComplete)))
    ];
}
