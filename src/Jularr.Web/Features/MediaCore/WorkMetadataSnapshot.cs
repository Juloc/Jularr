namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// One provider answer for one <c>(Work, locale)</c>, already normalized at the provider adapter boundary: provider strings, ids and
/// URLs that Jularr does not own stay out of it, except the image identity the artwork cache needs to download a chosen variant.
/// Empty values mean "the provider did not say"; they never erase what is stored.
/// </summary>
public sealed record WorkMetadataSnapshot(
    string Source,
    string ProviderExternalId,
    string Locale,
    string? Title,
    string? Overview,
    string? Tagline,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> TrailerKeys,
    string? OriginalTitle,
    string? OriginalLanguage,
    DateOnly? ReleaseDate,
    int? RuntimeMinutes,
    double? Rating,
    int? RatingCount,
    string? Certification,
    string? CertificationCountry,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> ProductionCountries,
    IReadOnlyList<WorkCreditCandidate> Credits,
    IReadOnlyList<WorkArtworkCandidate> Artwork);

public sealed record WorkCreditCandidate(WorkCreditKind Kind, string Name, string? Role, string? ProviderPersonId);

/// <param name="Language">ISO 639-1 language of text in the image; empty for neutral artwork.</param>
/// <param name="ProviderFilePath">The provider's identity of the image; stored for change detection, never served.</param>
/// <param name="DownloadUri">Where the artwork cache fetches it; built and validated by the provider adapter.</param>
public sealed record WorkArtworkCandidate(
    WorkArtworkSlot Slot,
    string Language,
    string ProviderFilePath,
    Uri DownloadUri,
    int? Width,
    int? Height,
    double? VoteAverage,
    int? VoteCount);

/// <summary>
/// The deterministic artwork choice rules (#820), used both when the spool decides which variants to keep and when a read model picks
/// the variant to show. Posters and logos carry text, so the viewer's language wins and neutral artwork is the fallback; backdrops are
/// best without text, so the highest-voted neutral backdrop wins. Ties break by votes, then size, then provider identity, never by
/// provider order.
/// </summary>
public static class WorkArtworkSelection
{
    private static readonly Comparer<WorkArtworkCandidate> CandidateQuality = Comparer<WorkArtworkCandidate>.Create((left, right) =>
    {
        var compared = (right.VoteAverage ?? 0).CompareTo(left.VoteAverage ?? 0);
        if (compared == 0)
        {
            compared = (right.VoteCount ?? 0).CompareTo(left.VoteCount ?? 0);
        }

        if (compared == 0)
        {
            compared = (right.Width ?? 0).CompareTo(left.Width ?? 0);
        }

        return compared != 0 ? compared : string.CompareOrdinal(left.ProviderFilePath, right.ProviderFilePath);
    });

    /// <summary>
    /// The language keys to try for a slot, most preferred first; the empty key is neutral artwork. <paramref name="preferredLanguages"/>
    /// comes from <see cref="WorkMetadataLocales.ArtworkLanguages"/>; any other language is only a last resort.
    /// </summary>
    public static IReadOnlyList<string> LanguagePreference(WorkArtworkSlot slot, IReadOnlyList<string> preferredLanguages)
    {
        var languages = preferredLanguages.ToList();
        if (slot == WorkArtworkSlot.Backdrop)
        {
            languages.Insert(0, "");
        }
        else
        {
            languages.Add("");
        }

        return languages;
    }

    /// <summary>
    /// The variants the spool keeps for one locale: the best image in the locale's language and the best neutral image. When the
    /// provider has neither, the best image of any language is kept so the slot is not empty. At most two per slot.
    /// </summary>
    public static IReadOnlyList<WorkArtworkCandidate> SelectForLocale(IEnumerable<WorkArtworkCandidate> candidates, string locale)
    {
        var language = WorkMetadataLocales.BaseLanguage(locale);
        var selected = new List<WorkArtworkCandidate>();
        foreach (var slot in candidates.GroupBy(x => x.Slot).OrderBy(x => x.Key))
        {
            var ranked = slot.OrderBy(x => x, CandidateQuality).ToArray();
            var forLanguage = ranked.FirstOrDefault(x => x.Language == language);
            var neutral = ranked.FirstOrDefault(x => x.Language.Length == 0);
            if (forLanguage is not null)
            {
                selected.Add(forLanguage);
            }

            if (neutral is not null)
            {
                selected.Add(neutral);
            }

            if (forLanguage is null && neutral is null)
            {
                selected.Add(ranked[0]);
            }
        }

        return selected;
    }

    /// <summary>The stored variant to show for a slot: the preferred languages and neutral artwork first, else the highest voted; null when the slot has none.</summary>
    public static T? Pick<T>(IEnumerable<T> variants, WorkArtworkSlot slot, IReadOnlyList<string> preferredLanguages, Func<T, string> language, Func<T, double?> votes)
        where T : class
    {
        var available = variants.ToArray();
        if (available.Length == 0)
        {
            return null;
        }

        foreach (var preferred in LanguagePreference(slot, preferredLanguages))
        {
            if (available.Where(x => language(x) == preferred).OrderByDescending(x => votes(x) ?? 0).FirstOrDefault() is { } match)
            {
                return match;
            }
        }

        return available.OrderByDescending(x => votes(x) ?? 0).ThenBy(x => language(x), StringComparer.Ordinal).First();
    }
}
