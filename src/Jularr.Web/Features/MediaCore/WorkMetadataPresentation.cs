namespace Jularr.Web.Features.MediaCore;

/// <summary>A locally cached artwork variant as a page or client addresses it; the URL is served by the Work artwork endpoint.</summary>
public sealed record WorkArtworkImage(string Url, int? Width, int? Height);

/// <param name="Role">The character for cast, the job for crew; null when the provider gives none.</param>
public sealed record WorkCreditView(string Name, string? Role);

public sealed record WorkTrailerView(string YouTubeKey)
{
    /// <summary>The only third-party origin a Work page ever frames, and only after the viewer starts the trailer.</summary>
    public const string EmbedOrigin = "https://www.youtube-nocookie.com";

    /// <summary>Whether the key has the shape of a YouTube video id; only such a key may become a link or an embed address.</summary>
    public bool IsPlayable => IsYouTubeKey(YouTubeKey);

    public string WatchUrl => $"https://www.youtube.com/watch?v={YouTubeKey}";

    /// <summary>The privacy-enhanced embed address, which sets no cookie until the viewer starts playback.</summary>
    public string EmbedUrl => $"{EmbedOrigin}/embed/{YouTubeKey}";

    public static bool IsYouTubeKey(string? value) => value is { Length: 11 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
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
    DateTime? RefreshedAt)
{
    /// <summary>The first trailer whose key may become a link or a frame; null when there is none.</summary>
    public WorkTrailerView? PlayableTrailer => Trailers.FirstOrDefault(x => x.IsPlayable);
}

/// <summary>The card-sized metadata of one Work: what a Library card shows from the persisted metadata, resolved for the viewer.</summary>
/// <param name="TrailerKey">The YouTube id of the first trailer of the resolved locale when it has the shape of one; the Discover Preview is the only card surface that uses it.</param>
public sealed record WorkCardMetadata(string? Title, string? PosterUrl, string? BackdropUrl, double? Rating, string? TrailerKey = null);

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

    /// <summary>
    /// The card metadata of every Work of a Library page, keyed by Work; Works without persisted metadata are absent. The title follows
    /// the same fallback as <see cref="Resolve"/>, so a card and its detail page always show the same title.
    /// </summary>
    public static IReadOnlyDictionary<Guid, WorkCardMetadata> ResolveCards(WorkCardMetadataRows rows, string viewerLocale)
    {
        var artworkLanguages = WorkMetadataLocales.ArtworkLanguages(viewerLocale, configuredFallback: null);
        var artwork = rows.Artwork.ToLookup(x => x.WorkId);
        var titles = rows.Titles.ToLookup(x => x.WorkId);
        var facts = rows.Facts.ToDictionary(x => x.WorkId);
        var trailers = rows.Trailers.ToLookup(x => x.WorkId);
        return rows.Artwork.Select(x => x.WorkId)
            .Concat(rows.Titles.Select(x => x.WorkId))
            .Concat(facts.Keys)
            .Distinct()
            .ToDictionary(
                workId => workId,
                workId =>
                {
                    facts.TryGetValue(workId, out var workFacts);
                    var order = WorkMetadataLocales.ResolutionOrder(viewerLocale, configuredFallback: null, workFacts?.OriginalLanguage);
                    var locale = WorkMetadataLocales.Pick([.. titles[workId].Select(x => x.Locale).Distinct(StringComparer.Ordinal)], order);
                    string? Url(WorkArtworkSlot slot) =>
                        WorkArtworkSelection.Pick(artwork[workId].Where(x => x.Slot == slot), slot, artworkLanguages, x => x.Language, x => x.VoteAverage) is { } chosen
                            ? ArtworkUrl(workId, chosen.ArtworkId, chosen.CacheKey)
                            : null;
                    var trailerLocale = WorkMetadataLocales.Pick([.. trailers[workId].Select(x => x.Locale).Distinct(StringComparer.Ordinal)], order);
                    var trailerKey = trailers[workId].FirstOrDefault(x => x.Locale == trailerLocale)?.Key;
                    return new WorkCardMetadata(
                        titles[workId].FirstOrDefault(x => x.Locale == locale)?.Value,
                        Url(WorkArtworkSlot.Poster),
                        Url(WorkArtworkSlot.Backdrop),
                        workFacts?.Rating,
                        WorkTrailerView.IsYouTubeKey(trailerKey) ? trailerKey : null);
                });
    }
}
