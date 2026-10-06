namespace Jularr.Web.Features.MediaCore;

/// <summary>A locally cached artwork variant as a page or client addresses it; the URL is served by the Work artwork endpoint.</summary>
public sealed record WorkArtworkImage(string Url, int? Width, int? Height);

/// <param name="Role">The character for cast, the job for crew; null when the provider gives none.</param>
public sealed record WorkCreditView(string Name, string? Role);

public sealed record WorkTrailerView(string YouTubeKey)
{
    public string WatchUrl => $"https://www.youtube.com/watch?v={YouTubeKey}";

    /// <summary>The privacy-enhanced embed address, which sets no cookie until the viewer starts playback.</summary>
    public string EmbedUrl => $"https://www.youtube-nocookie.com/embed/{YouTubeKey}";
}

/// <summary>
/// The persisted metadata of one Work resolved for one viewer locale, ready for a detail page. Every value is local; null or empty
/// means it is not known yet, and pages omit the section. Each text field falls back on its own (a German title with an English
/// synopsis), so a missing translation never hides a value that exists in another locale.
/// </summary>
public sealed record WorkMetadataView(
    string? Title,
    string? OriginalTitle,
    string? Overview,
    string? Tagline,
    IReadOnlyList<string> Genres,
    DateOnly? ReleaseDate,
    int? RuntimeMinutes,
    double? Rating,
    int? RatingCount,
    string? Certification,
    string? CertificationCountry,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> ProductionCountries,
    IReadOnlyList<WorkTrailerView> Trailers,
    IReadOnlyList<WorkCreditView> Cast,
    IReadOnlyList<WorkCreditView> Crew,
    WorkArtworkImage? Poster,
    WorkArtworkImage? Backdrop,
    WorkArtworkImage? Logo,
    DateTime? RefreshedAt);

/// <summary>The card-sized metadata of one Work: what a Library card shows besides its own facts.</summary>
public sealed record WorkCardMetadata(string? PosterUrl, string? BackdropUrl, double? Rating);

/// <summary>
/// Turns persisted metadata rows into what pages show, applying <see cref="WorkMetadataLocales"/> and <see cref="WorkArtworkSelection"/>.
/// Pure and side-effect free: resolving a locale that is not stored never enqueues anything (the page asks for that explicitly).
/// </summary>
public static class WorkMetadataPresentation
{
    /// <summary>
    /// The address of a cached variant. The version is a prefix of the cache key, which changes whenever the variant's image changes,
    /// so the endpoint can let browsers keep the response for good.
    /// </summary>
    public static string ArtworkUrl(Guid workId, long artworkId, string cacheKey) => $"/works/{workId:D}/artwork/{artworkId}?v={cacheKey[..12]}";

    /// <summary>The view for <paramref name="viewerLocale"/>; null when nothing is persisted for the Work yet.</summary>
    public static WorkMetadataView? Resolve(Guid workId, WorkMetadataRows rows, string viewerLocale)
    {
        if (rows.Facts is null && rows.Values.Count == 0 && rows.Credits.Count == 0 && rows.Artwork.Count == 0)
        {
            return null;
        }

        var order = WorkMetadataLocales.ResolutionOrder(viewerLocale, configuredFallback: null, rows.Facts?.OriginalLanguage);
        var artworkLanguages = WorkMetadataLocales.ArtworkLanguages(viewerLocale, configuredFallback: null);
        var byField = rows.Values.ToLookup(x => x.Field);
        IReadOnlyList<string> Texts(WorkLocalizedField field)
        {
            var values = byField[field].ToArray();
            var locale = WorkMetadataLocales.Pick([.. values.Select(x => x.Locale).Distinct(StringComparer.Ordinal)], order);
            return [.. values.Where(x => x.Locale == locale).OrderBy(x => x.Position).Select(x => x.Value)];
        }

        WorkArtworkImage? Image(WorkArtworkSlot slot) =>
            WorkArtworkSelection.Pick(rows.Artwork.Where(x => x.Slot == slot), slot, artworkLanguages, x => x.Language, x => x.VoteAverage) is { CacheKey: { } key } chosen
                ? new WorkArtworkImage(ArtworkUrl(workId, chosen.Id, key), chosen.Width, chosen.Height)
                : null;

        var facts = rows.Facts;
        return new WorkMetadataView(
            Texts(WorkLocalizedField.Title).FirstOrDefault(),
            facts?.OriginalTitle,
            Texts(WorkLocalizedField.Overview).FirstOrDefault(),
            Texts(WorkLocalizedField.Tagline).FirstOrDefault(),
            Texts(WorkLocalizedField.Genre),
            facts?.ReleaseDate,
            facts?.RuntimeMinutes,
            facts?.Rating,
            facts?.RatingCount,
            facts?.Certification,
            facts?.CertificationCountry,
            facts?.Studios ?? [],
            facts?.ProductionCountries ?? [],
            [.. Texts(WorkLocalizedField.Trailer).Select(x => new WorkTrailerView(x))],
            [.. rows.Credits.Where(x => x.Kind == WorkCreditKind.Cast).OrderBy(x => x.Position).Select(x => new WorkCreditView(x.Name, x.Role))],
            [.. rows.Credits.Where(x => x.Kind == WorkCreditKind.Crew).OrderBy(x => x.Position).Select(x => new WorkCreditView(x.Name, x.Role))],
            Image(WorkArtworkSlot.Poster),
            Image(WorkArtworkSlot.Backdrop),
            Image(WorkArtworkSlot.Logo),
            rows.LastSucceededAt);
    }

    /// <summary>The card metadata of every Work of a Library page, keyed by Work; Works without persisted metadata are absent.</summary>
    public static IReadOnlyDictionary<Guid, WorkCardMetadata> ResolveCards(IEnumerable<WorkCardMetadataRow> rows, string viewerLocale)
    {
        var artworkLanguages = WorkMetadataLocales.ArtworkLanguages(viewerLocale, configuredFallback: null);
        return rows
            .GroupBy(x => x.WorkId)
            .ToDictionary(
                work => work.Key,
                work =>
                {
                    var variants = work.Where(x => x is { ArtworkId: not null, CacheKey: not null, Slot: not null }).ToArray();
                    string? Url(WorkArtworkSlot slot) =>
                        WorkArtworkSelection.Pick(variants.Where(x => x.Slot == slot), slot, artworkLanguages, x => x.Language ?? "", x => x.VoteAverage) is { } chosen
                            ? ArtworkUrl(work.Key, chosen.ArtworkId!.Value, chosen.CacheKey!)
                            : null;
                    return new WorkCardMetadata(Url(WorkArtworkSlot.Poster), Url(WorkArtworkSlot.Backdrop), work.Select(x => x.Rating).FirstOrDefault(x => x is not null));
                });
    }
}
