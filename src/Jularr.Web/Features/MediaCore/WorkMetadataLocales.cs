using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Localization;

namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// The one owner of metadata locale semantics (#820): locale normalization, the locale the background spool fetches, and the
/// deterministic display fallback every read model uses. A locale only ever selects among values of one Work; it never creates or
/// merges Works.
/// </summary>
public static class WorkMetadataLocales
{
    /// <summary>
    /// The instance metadata locale the spool fetches. Until the Admin General language policy (#820 Fixed/Free mode) exists, the
    /// instance default is the UI source locale; it is the only required metadata locale in slice 1.
    /// </summary>
    public const string InstanceDefault = UiTranslationCatalog.SourceLocale;

    public const string English = "en";

    /// <summary>
    /// The metadata locale a profile reads in. UI and metadata share one language policy (#820); until its Fixed/Free modes exist this
    /// is the profile's UI locale, which itself falls back to the instance default.
    /// </summary>
    public static async Task<string> ForProfileAsync(AppDbContext db, string profileId, CancellationToken cancellationToken) =>
        (await new UiTranslationCatalogStore(db).GetProfileLocaleAsync(profileId, cancellationToken)).Locale;

    /// <summary>The culture name of a BCP-47 tag (<c>de_at</c> becomes <c>de-AT</c>); null when the runtime does not know it.</summary>
    public static string? Normalize(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return null;
        }

        try
        {
            var name = CultureInfo.GetCultureInfo(locale.Trim().Replace('_', '-')).Name;
            return name.Length == 0 ? null : name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The neutral language of a locale (<c>de-AT</c> becomes <c>de</c>), lower case.</summary>
    public static string BaseLanguage(string locale)
    {
        var dash = locale.IndexOf('-');
        return (dash < 0 ? locale : locale[..dash]).ToLowerInvariant();
    }

    /// <summary>
    /// The region whose age rating applies to a locale: its own region, else the default region of its language (<c>en</c> resolves
    /// to <c>US</c>, <c>de</c> to <c>DE</c>). Null when the runtime cannot derive one.
    /// </summary>
    public static string? CertificationCountry(string locale)
    {
        try
        {
            var specific = CultureInfo.CreateSpecificCulture(locale);
            return specific.Name.Length == 0 ? null : new RegionInfo(specific.Name).TwoLetterISORegionName;
        }
        catch (Exception exception) when (exception is CultureNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The display fallback order: the exact locale, its parent cultures, the configured fallback and its parents, English, then the
    /// original language. Duplicate steps are skipped. After the last step <see cref="Pick"/> still takes any locally stored value.
    /// </summary>
    public static IReadOnlyList<string> ResolutionOrder(string requested, string? configuredFallback, string? originalLanguage)
    {
        var order = new List<string>();
        AddWithParents(order, Normalize(requested));
        AddWithParents(order, Normalize(configuredFallback));
        Add(order, English);
        Add(order, Normalize(originalLanguage));
        return order;
    }

    /// <summary>
    /// The languages artwork with text is preferred in: the viewer's language family, then the configured fallback's. Unlike text,
    /// English and the original language are not inserted here: neutral artwork beats a poster in a language the viewer did not ask
    /// for (#820), see <see cref="WorkArtworkSelection.LanguagePreference"/>.
    /// </summary>
    public static IReadOnlyList<string> ArtworkLanguages(string requested, string? configuredFallback)
    {
        var order = new List<string>();
        AddWithParents(order, Normalize(requested));
        AddWithParents(order, Normalize(configuredFallback));
        return [.. order.Select(BaseLanguage).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The stored locale to display: the first step of <paramref name="order"/> that is stored exactly; a neutral-language step also
    /// matches a stored regional sibling (<c>de</c> takes <c>de-DE</c>), ordinal first. When no step matches, the best locally available
    /// value is the ordinally first stored locale, so the choice never depends on row order. Null only when nothing is stored.
    /// </summary>
    public static string? Pick(IReadOnlyCollection<string> available, IReadOnlyList<string> order)
    {
        if (available.Count == 0)
        {
            return null;
        }

        var sorted = available.Order(StringComparer.Ordinal).ToArray();
        foreach (var step in order)
        {
            if (sorted.FirstOrDefault(x => string.Equals(x, step, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return exact;
            }

            if (!step.Contains('-') && sorted.FirstOrDefault(x => string.Equals(BaseLanguage(x), step, StringComparison.OrdinalIgnoreCase)) is { } sibling)
            {
                return sibling;
            }
        }

        return sorted[0];
    }

    private static void AddWithParents(List<string> order, string? locale)
    {
        for (var culture = locale is null ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(locale); culture.Name.Length > 0; culture = culture.Parent)
        {
            Add(order, culture.Name);
        }
    }

    private static void Add(List<string> order, string? locale)
    {
        if (locale is not null && !order.Contains(locale, StringComparer.OrdinalIgnoreCase))
        {
            order.Add(locale);
        }
    }
}
