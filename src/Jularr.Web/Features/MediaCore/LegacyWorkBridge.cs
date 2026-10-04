using Jularr.Web.Data;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Mapping;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Tv;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// Non-invasive adapters that unify the existing per-type entities (Anime, NovelWork, BookEdition,
/// MangaSeries) under the universal <see cref="Work"/> model (#592). Each method ensures a work exists
/// for a legacy record and bridges it through a <see cref="WorkSourceLink"/> — the legacy tables are
/// never modified, so every current feature keeps working. Bridging is idempotent: the same legacy
/// record always resolves to the same work.
///
/// The per-type <b>population</b> from providers (rich metadata, full external-id sets, structure) is
/// deliberately minimal here and completed by the individual #556 library children; this class proves
/// the bridge and gives those children a stable entry point.
/// </summary>
public sealed class LegacyWorkBridge(AppDbContext db, WorkService works, WorkStructureService structure)
{
    /// <summary>Ensures the work for an anime record (bridges by <c>Anime.Id</c>).</summary>
    public async Task<Guid> EnsureWorkForAnimeAsync(Anime anime, CancellationToken cancellationToken)
    {
        var workId = await EnsureWorkAsync(
            WorkSourceKind.Anime, anime.Id, WorkMediaType.Anime, anime.Title, year: null, cancellationToken);

        await works.AddOrUpdateTitleAsync(
            workId, WorkTitleType.Primary, "und", anime.Title, MetadataFieldSources.Local, isPrimary: true, cancellationToken);
        // #435: record where the field came from so a later provider refresh respects the precedence
        // ladder (and never clobbers an owner correction). AniList is the built-in display-metadata role.
        await works.SetFieldProvenanceAsync(
            workId, "title", MetadataFieldSources.Local, null, null,
            isManualOverride: false, preferredProvider: MappingProviders.AniList, cancellationToken);
        return workId;
    }

    /// <summary>
    /// Ensures the work for a movie record (bridges by <c>Movie.Id</c>) and mirrors its title and its
    /// TMDB/IMDb identities into the core. A movie is a single unit, so no season/episode structure is
    /// created (#593).
    /// </summary>
    public async Task<Guid> EnsureWorkForMovieAsync(Movie movie, CancellationToken cancellationToken)
    {
        var workId = !string.IsNullOrWhiteSpace(movie.TmdbId)
            ? await EnsureWorkWithExternalIdentityAsync(
                WorkSourceKind.Movie,
                movie.Id,
                WorkMediaType.Movie,
                movie.Title,
                movie.Year,
                MappingProviders.Tmdb,
                movie.TmdbId!,
                cancellationToken)
            : await EnsureWorkAsync(
                WorkSourceKind.Movie, movie.Id, WorkMediaType.Movie, movie.Title, movie.Year, cancellationToken);

        await works.AddOrUpdateTitleAsync(
            workId, WorkTitleType.Primary, "und", movie.Title, MetadataFieldSources.Local, isPrimary: true, cancellationToken);
        await works.SetFieldProvenanceAsync(
            workId, "title", MetadataFieldSources.Local, null, null,
            isManualOverride: false, preferredProvider: MappingProviders.Tmdb, cancellationToken);

        if (!string.IsNullOrWhiteSpace(movie.TmdbId))
        {
            await works.LinkExternalIdentityAsync(
                workId, WorkMediaType.Movie, MappingProviders.Tmdb, movie.TmdbId!,
                confidence: 1.0, evidence: "movie TMDB id", isPrimary: true,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(movie.ImdbId))
        {
            await works.LinkExternalIdentityAsync(
                workId, WorkMediaType.Movie, MappingProviders.Imdb, movie.ImdbId!,
                confidence: 1.0, evidence: "movie IMDb id", isPrimary: false,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        return workId;
    }

    /// <summary>
    /// Ensures the work for a TV series record (bridges by <c>TvSeries.Id</c>) and mirrors its title and
    /// its TMDB/TVDB identities into the core. A series reuses the universal season/episode structure
    /// (<see cref="WorkSeason"/>/<see cref="WorkEpisode"/>), so the caller adds those through
    /// <see cref="WorkStructureService"/> rather than a per-type episode table (#594).
    /// </summary>
    public async Task<Guid> EnsureWorkForSeriesAsync(TvSeries series, CancellationToken cancellationToken)
    {
        var workId = !string.IsNullOrWhiteSpace(series.TmdbId)
            ? await EnsureWorkWithExternalIdentityAsync(
                WorkSourceKind.Series,
                series.Id,
                WorkMediaType.Series,
                series.Title,
                series.Year,
                MappingProviders.Tmdb,
                series.TmdbId!,
                cancellationToken)
            : await EnsureWorkAsync(
                WorkSourceKind.Series, series.Id, WorkMediaType.Series, series.Title, series.Year, cancellationToken);

        await works.AddOrUpdateTitleAsync(
            workId, WorkTitleType.Primary, "und", series.Title, MetadataFieldSources.Local, isPrimary: true, cancellationToken);
        await works.SetFieldProvenanceAsync(
            workId, "title", MetadataFieldSources.Local, null, null,
            isManualOverride: false, preferredProvider: MappingProviders.Tmdb, cancellationToken);

        if (!string.IsNullOrWhiteSpace(series.TmdbId))
        {
            await works.LinkExternalIdentityAsync(
                workId, WorkMediaType.Series, MappingProviders.Tmdb, series.TmdbId!,
                confidence: 1.0, evidence: "series TMDB id", isPrimary: true,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(series.TvdbId))
        {
            await works.LinkExternalIdentityAsync(
                workId, WorkMediaType.Series, MappingProviders.Tvdb, series.TvdbId!,
                confidence: 1.0, evidence: "series TVDB id", isPrimary: false,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        return workId;
    }

    /// <summary>The media-core edition key/format and version key an audiobook release is bridged as.</summary>
    public const string AudiobookEditionKey = "audiobook";
    public const string AudiobookEditionFormat = "audiobook";
    public const string AudiobookVersionKey = "audiobook";

    /// <summary>
    /// Ensures the work for an audiobook record (bridges by <c>Audiobook.Id</c>). An audiobook is the
    /// audio edition of a book, so the work is a <see cref="WorkMediaType.Book"/>; the audiobook release
    /// is modelled as a <see cref="WorkEdition"/> (format <c>audiobook</c>) owning a
    /// <see cref="WorkVersion"/> — editions and versions are modelled separately (#592). The Audible ASIN,
    /// when known, is mirrored as a correctable external identity (#440).
    /// </summary>
    public async Task<Guid> EnsureWorkForAudiobookAsync(Audiobook audiobook, CancellationToken cancellationToken)
    {
        var workId = await EnsureWorkAsync(
            WorkSourceKind.Audiobook, audiobook.Id, WorkMediaType.Book, audiobook.Title, audiobook.Year, cancellationToken);

        await works.AddOrUpdateTitleAsync(
            workId, WorkTitleType.Primary, "und", audiobook.Title, MetadataFieldSources.Local, isPrimary: true, cancellationToken);
        await works.SetFieldProvenanceAsync(
            workId, "title", MetadataFieldSources.Local, null, null,
            isManualOverride: false, preferredProvider: null, cancellationToken);

        var edition = await structure.AddOrUpdateEditionAsync(
            workId,
            editionKey: AudiobookEditionKey,
            language: "und",
            format: AudiobookEditionFormat,
            publisher: null,
            isbn13: null,
            title: string.IsNullOrWhiteSpace(audiobook.Narrator) ? null : $"Narrated by {audiobook.Narrator!.Trim()}",
            isPrimary: true,
            cancellationToken);

        await structure.AddOrUpdateVersionAsync(
            workId,
            versionKey: AudiobookVersionKey,
            unitKey: null,
            editionId: edition.Id,
            quality: null,
            releaseGroup: null,
            source: AudiobookEditionFormat,
            notes: audiobook.Narrator is { Length: > 0 } narrator ? $"Narrator: {narrator.Trim()}" : null,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(audiobook.Asin))
        {
            await works.LinkExternalIdentityAsync(
                workId, WorkMediaType.Book, "audible", audiobook.Asin!,
                confidence: 1.0, evidence: "audiobook ASIN", isPrimary: false,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        return workId;
    }

    /// <summary>
    /// Ensures the work for a novel/light-novel record and mirrors its source and metadata provider
    /// identities plus its titles into the core. The caller decides whether the work is a
    /// <see cref="WorkMediaType.LightNovel"/> or a plain <see cref="WorkMediaType.Book"/>.
    /// </summary>
    public async Task<Guid> EnsureWorkForNovelAsync(
        NovelWork novel,
        WorkMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var workId = await EnsureWorkAsync(
            WorkSourceKind.NovelWork, novel.Id, mediaType, novel.Title, year: null, cancellationToken);

        await works.AddOrUpdateTitleAsync(
            workId, WorkTitleType.Primary, "und", novel.Title, novel.SourceProvider, isPrimary: true, cancellationToken);
        await works.SetFieldProvenanceAsync(
            workId, "title", novel.SourceProvider, novel.SourceKey, null,
            isManualOverride: false, preferredProvider: novel.MetadataProvider ?? novel.SourceProvider, cancellationToken);

        if (!string.IsNullOrWhiteSpace(novel.MetadataNativeTitle))
        {
            await works.AddOrUpdateTitleAsync(
                workId, WorkTitleType.Native, "und", novel.MetadataNativeTitle!,
                novel.MetadataProvider ?? "", isPrimary: false, cancellationToken);
            await works.SetFieldProvenanceAsync(
                workId, "originalTitle", novel.MetadataProvider ?? "", novel.MetadataExternalId, null,
                isManualOverride: false, preferredProvider: novel.MetadataProvider ?? "", cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(novel.SourceProvider) && !string.IsNullOrWhiteSpace(novel.SourceKey))
        {
            await works.LinkExternalIdentityAsync(
                workId, mediaType, novel.SourceProvider, novel.SourceKey,
                confidence: 1.0, evidence: "novel source key", isPrimary: true,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(novel.MetadataProvider) && !string.IsNullOrWhiteSpace(novel.MetadataExternalId))
        {
            await works.LinkExternalIdentityAsync(
                workId, mediaType, novel.MetadataProvider!, novel.MetadataExternalId!,
                confidence: 1.0, evidence: "novel metadata id", isPrimary: false,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        return workId;
    }

    /// <summary>
    /// Ensures the work for a book by bridging its parent novel work, and mirrors the book edition into
    /// a <see cref="WorkEdition"/> (editions are modelled separately from versions, #592).
    /// </summary>
    public async Task<Guid> EnsureWorkForBookEditionAsync(BookEdition edition, CancellationToken cancellationToken)
    {
        var novel = await db.Set<NovelWork>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == edition.WorkId, cancellationToken)
            ?? throw new InvalidOperationException($"Book edition {edition.Id:D} has no parent novel work.");

        var workId = await EnsureWorkForNovelAsync(novel, WorkMediaType.Book, cancellationToken);

        await structure.AddOrUpdateEditionAsync(
            workId,
            editionKey: edition.EditionKey,
            language: edition.Language,
            format: null,
            publisher: edition.Publisher,
            isbn13: edition.Isbn13,
            title: edition.Title,
            isPrimary: edition.IsPrimary,
            cancellationToken);

        // Bridge the edition record itself so it resolves to the same work.
        await works.LinkSourceAsync(workId, WorkSourceKind.BookEdition, edition.Id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(edition.Isbn13))
        {
            await works.LinkExternalIdentityAsync(
                workId, WorkMediaType.Book, "isbn", edition.Isbn13!,
                confidence: 1.0, evidence: "book ISBN-13", isPrimary: false,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        return workId;
    }

    /// <summary>
    /// Ensures the work for a manga series. MangaSeries is a raw-SQL table (not an EF entity), so the
    /// caller passes the fields; the bridge is keyed by the series id.
    /// </summary>
    public async Task<Guid> EnsureWorkForMangaSeriesAsync(
        Guid seriesId,
        string title,
        string? nativeTitle,
        string? aniListId,
        CancellationToken cancellationToken)
    {
        var workId = await EnsureWorkAsync(
            WorkSourceKind.MangaSeries, seriesId, WorkMediaType.Manga, title, year: null, cancellationToken);

        await works.AddOrUpdateTitleAsync(
            workId, WorkTitleType.Primary, "und", title, MetadataFieldSources.Local, isPrimary: true, cancellationToken);
        await works.SetFieldProvenanceAsync(
            workId, "title", MetadataFieldSources.Local, null, null,
            isManualOverride: false, preferredProvider: MappingProviders.AniList, cancellationToken);

        if (!string.IsNullOrWhiteSpace(nativeTitle))
        {
            await works.AddOrUpdateTitleAsync(
                workId, WorkTitleType.Native, "und", nativeTitle!, MappingProviders.AniList, isPrimary: false, cancellationToken);
            await works.SetFieldProvenanceAsync(
                workId, "originalTitle", MappingProviders.AniList, aniListId, null,
                isManualOverride: false, preferredProvider: MappingProviders.AniList, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(aniListId))
        {
            await works.LinkExternalIdentityAsync(
                workId, WorkMediaType.Manga, MappingProviders.AniList, aniListId!,
                confidence: 1.0, evidence: "manga AniList id", isPrimary: true,
                isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        return workId;
    }

    /// <summary>
    /// Resolves a legacy row and a strong provider identity to one Work. If both already point at
    /// different Works the mapping is ambiguous and must be reviewed instead of silently duplicating.
    /// </summary>
    private async Task<Guid> EnsureWorkWithExternalIdentityAsync(
        WorkSourceKind sourceKind,
        Guid sourceId,
        WorkMediaType mediaType,
        string title,
        int? year,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        var sourceWorkId = await db.Set<WorkSourceLink>().AsNoTracking()
            .Where(x => x.SourceKind == sourceKind && x.SourceId == sourceId)
            .Select(x => (Guid?)x.WorkId)
            .FirstOrDefaultAsync(cancellationToken);
        var identityWorkId = await db.Set<WorkExternalIdentity>().AsNoTracking()
            .Where(x =>
                x.MediaType == mediaType
                && x.Provider == provider
                && x.ExternalId == externalId.Trim())
            .Select(x => (Guid?)x.WorkId)
            .SingleOrDefaultAsync(cancellationToken);

        if (sourceWorkId is { } source && identityWorkId is { } identity && source != identity)
        {
            throw new InvalidOperationException(
                $"Legacy {sourceKind} {sourceId} and {provider}:{externalId} resolve to different Works and require review.");
        }

        if (identityWorkId is { } existingIdentity)
        {
            await works.LinkSourceAsync(existingIdentity, sourceKind, sourceId, cancellationToken);
            return existingIdentity;
        }

        if (sourceWorkId is { } existingSource)
        {
            return existingSource;
        }

        var work = await works.CreateWorkAsync(mediaType, title, year, cancellationToken);
        await works.LinkSourceAsync(work.Id, sourceKind, sourceId, cancellationToken);
        return work.Id;
    }

    /// <summary>Resolves the existing work bridged to a legacy record, creating and linking one if absent.</summary>
    private async Task<Guid> EnsureWorkAsync(
        WorkSourceKind sourceKind,
        Guid sourceId,
        WorkMediaType mediaType,
        string title,
        int? year,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<WorkSourceLink>().AsNoTracking()
            .Where(x => x.SourceKind == sourceKind && x.SourceId == sourceId)
            .Select(x => (Guid?)x.WorkId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } workId)
        {
            return workId;
        }

        var work = await works.CreateWorkAsync(mediaType, title, year, cancellationToken);
        await works.LinkSourceAsync(work.Id, sourceKind, sourceId, cancellationToken);
        return work.Id;
    }
}
