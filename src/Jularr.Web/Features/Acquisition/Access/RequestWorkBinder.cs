using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// Binds Book, Light Novel and Manga requests to the canonical Work they are about (#396, #432). The Work is the one the Library, the profile
/// assignment, the search and the importer use; the provider and external id of the request are only the evidence it is resolved from, and the
/// title is never evidence. Resolution is deterministic and idempotent: the same evidence always gives the same Work, a Work that already exists is
/// reused (by provider identity first, then by the legacy record that already holds the id), and evidence that points at two different Works is
/// not guessed at, the request stays unbound and keeps working on its payload. Nothing is created by showing a search result: a Work is only
/// materialized for a request, which is a durable decision.
/// </summary>
public sealed class RequestWorkBinder(AppDbContext db, WorkService works, LegacyWorkBridge bridge, AcquisitionAccessStore store, ILogger<RequestWorkBinder> logger, MonitoringResolver? monitoringState = null, MonitoringCommands? monitoring = null)
{
    /// <summary>The media types whose requests carry a canonical Work (Movie and TV keep theirs in the video payload, Anime in its own monitoring).</summary>
    public static bool Applies(MediaAcquisitionKind kind) => kind is MediaAcquisitionKind.Book or MediaAcquisitionKind.Audiobook or MediaAcquisitionKind.LightNovel or MediaAcquisitionKind.Manga;

    public static WorkMediaType MediaTypeOf(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Book or MediaAcquisitionKind.Audiobook => WorkMediaType.Book,
        MediaAcquisitionKind.LightNovel => WorkMediaType.LightNovel,
        MediaAcquisitionKind.Manga => WorkMediaType.Manga,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This media type has no request Work binding.")
    };

    /// <summary>
    /// Whether the provider evidence of a request is a stable identity of a Work: an AniList id for Manga and Light Novels, a Syosetu key for a web
    /// novel, a catalog id for Books. Anything else (a transient search id, an empty value) is not, and gives no Work.
    /// </summary>
    public static bool IsTrustworthy(MediaAcquisitionKind kind, string provider, string externalId)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalId))
        {
            return false;
        }

        return kind switch
        {
            MediaAcquisitionKind.Book or MediaAcquisitionKind.Audiobook => provider.Equals(BookCatalogService.CatalogRequestProvider, StringComparison.OrdinalIgnoreCase),
            MediaAcquisitionKind.Manga => provider.Equals(NovelAniListProvider.ProviderKey, StringComparison.OrdinalIgnoreCase) && long.TryParse(externalId, out var id) && id > 0,
            MediaAcquisitionKind.LightNovel => (provider.Equals(NovelAniListProvider.ProviderKey, StringComparison.OrdinalIgnoreCase) && long.TryParse(externalId, out var anilist) && anilist > 0)
                || provider.Equals(NcodeNovelSourceProvider.ProviderKey, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>
    /// The canonical Work of the evidence, or null when the evidence is not trustworthy or points at two different Works. The Work is created only when
    /// nothing identifies one yet.
    /// </summary>
    public async Task<long?> ResolveAsync(MediaAcquisitionKind kind, string provider, string externalId, string title, CancellationToken cancellationToken, IReadOnlyList<(string Provider, string ExternalId)>? alsoKnownAs = null)
    {
        if (!Applies(kind) || !IsTrustworthy(kind, provider, externalId))
        {
            return null;
        }

        var mediaType = MediaTypeOf(kind);
        var byIdentity = await WorkOfIdentityAsync(mediaType, provider, externalId, cancellationToken);
        var byLegacy = await WorkOfLegacyRecordAsync(kind, provider, externalId, cancellationToken);
        if (byIdentity is { } known && byLegacy is { } legacy && known != legacy)
        {
            logger.LogWarning("Request evidence {Kind}/{Provider}/{ExternalId} points at two Works ({First}, {Second}); not binding.", kind, provider, externalId, known, legacy);
            return null;
        }

        // Another provider's record of the same book (or its ISBN) may already have its Work: exactly one such Work is this book's; none leaves the book new,
        // and more than one is an existing split that is not decided here.
        long? resolved = byIdentity ?? byLegacy;
        if (resolved is null && alsoKnownAs is { Count: > 0 })
        {
            var holders = new HashSet<long>();
            foreach (var (otherProvider, otherId) in alsoKnownAs)
            {
                if (await WorkOfIdentityAsync(mediaType, otherProvider, otherId, cancellationToken) is { } holder)
                {
                    holders.Add(holder);
                }
            }

            resolved = holders.Count == 1 ? holders.Single() : null;
        }

        if (resolved is { } existing)
        {
            // The identity may still be unlinked when the Work was found through its legacy record; linking is a no-op when it is already there.
            await works.LinkExternalIdentityAsync(existing, mediaType, provider, externalId, 1.0, "request provider id", false, false, MappingReviewState.Confirmed, cancellationToken);
            await LinkAlsoKnownAsAsync(mediaType, existing, alsoKnownAs, cancellationToken);
            await MonitorRequestedWorkAsync(kind, existing, cancellationToken);
            return existing;
        }

        var created = (await works.EnsureWorkByExternalIdentityAsync(mediaType, provider, externalId, title, null, cancellationToken)).Id;
        await LinkAlsoKnownAsAsync(mediaType, created, alsoKnownAs, cancellationToken);
        await MonitorRequestedWorkAsync(kind, created, cancellationToken);
        return created;
    }

    /// <summary>The other identities a request names for its title: the provider records of the same book and its ISBN, as the Books add dialog sends them.</summary>
    public static IReadOnlyList<(string Provider, string ExternalId)> AlsoKnownAs(MediaAcquisitionKind kind, string? payloadJson)
    {
        if (kind != MediaAcquisitionKind.Book || string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        try
        {
            if (System.Text.Json.JsonSerializer.Deserialize<BookRequestPayload>(payloadJson, System.Text.Json.JsonSerializerOptions.Web) is not { } payload)
            {
                return [];
            }

            var identities = (payload.Identities ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && id != payload.CatalogId).Select(id => (BookCatalogService.CatalogRequestProvider, id.Trim()));
            return [.. identities.Concat(BookReleaseSelector.NormalizeIsbn(payload.Isbn) is { } isbn ? [("isbn", isbn)] : [])];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    // An identity another Work holds stays there; the link only adds what nothing claimed yet.
    private async Task LinkAlsoKnownAsAsync(WorkMediaType mediaType, long workId, IReadOnlyList<(string Provider, string ExternalId)>? alsoKnownAs, CancellationToken cancellationToken)
    {
        foreach (var (otherProvider, otherId) in alsoKnownAs ?? [])
        {
            await works.LinkExternalIdentityAsync(workId, mediaType, otherProvider, otherId, 1.0, "same book, another provider record", false, false, MappingReviewState.Confirmed, cancellationToken);
        }
    }

    // Requesting a Book or Manga monitors it unless somebody already decided: that is what keeps a better format of a Book and the volumes still missing of a
    // Manga wanted after the first import. An audiobook request says nothing about the Book, and a decision the owner made (also "off") is never overwritten.
    private async Task MonitorRequestedWorkAsync(MediaAcquisitionKind kind, long workId, CancellationToken cancellationToken)
    {
        if (kind is MediaAcquisitionKind.Book or MediaAcquisitionKind.Manga && monitoring is not null && monitoringState is not null && (await monitoringState.LoadAsync(workId, cancellationToken)).WorkDecision is null)
        {
            await monitoring.SetWorkAsync(workId, true, cancellationToken, replaceChildren: false);
        }
    }

    /// <summary>Binds a request that has no Work yet; a request that has one, or whose evidence cannot be resolved safely, is returned as it is.</summary>
    public async Task<AcquisitionRequest> EnsureBoundAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (request.WorkId is not null || !Applies(request.Kind))
        {
            return request;
        }

        if (await ResolveAsync(request.Kind, request.Provider, request.ExternalId, request.Title, cancellationToken, AlsoKnownAs(request.Kind, request.PayloadJson)) is not { } workId)
        {
            return request;
        }

        await store.BindWorkAsync(request.Id, workId, cancellationToken);

        return (await store.GetAsync(request.Id, cancellationToken)) ?? request with { WorkId = workId };
    }

    /// <summary>The record of a legacy library (Manga series, Novel work) already linked to the Work, so an import goes into it instead of creating a second one.</summary>
    public async Task<Guid?> LinkedLegacyIdAsync(long workId, WorkSourceKind sourceKind, CancellationToken cancellationToken) =>
        await db.Set<WorkSourceLink>().AsNoTracking()
            .Where(link => link.WorkId == workId && link.SourceKind == sourceKind)
            .OrderBy(link => link.SourceId)
            .Select(link => (Guid?)link.SourceId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The legacy record an import of this request goes into: the one already linked to the request's Work, or else the one that already holds the request's
    /// provider id when it belongs to no other Work. Null when there is none yet, and the importer creates one that <see cref="BindImportedAsync"/> then binds.
    /// <paramref name="novelSourceProvider"/> limits Novel records to a source the importer can add files to (EPUB series, imported books).
    /// </summary>
    public async Task<Guid?> LegacyTargetAsync(AcquisitionRequest request, WorkSourceKind sourceKind, string? novelSourceProvider, CancellationToken cancellationToken)
    {
        request = await EnsureBoundAsync(request, cancellationToken);
        if (request.WorkId is not { } workId)
        {
            return null;
        }

        if (await LinkedLegacyIdAsync(workId, sourceKind, cancellationToken) is { } linked
            && (novelSourceProvider is null || await db.NovelWorks.AnyAsync(novel => novel.Id == linked && novel.SourceProvider == novelSourceProvider, cancellationToken)))
        {
            return linked;
        }

        var holder = sourceKind == WorkSourceKind.MangaSeries
            ? (await new MangaRepository(db).FindByMetadataAsync(request.Provider, request.ExternalId, cancellationToken))?.Id
            : await db.NovelWorks.AsNoTracking()
                .Where(novel => novel.MetadataProvider == request.Provider && novel.MetadataExternalId == request.ExternalId && (novelSourceProvider == null || novel.SourceProvider == novelSourceProvider))
                .Select(novel => (Guid?)novel.Id)
                .FirstOrDefaultAsync(cancellationToken);
        if (holder is not { } id)
        {
            return null;
        }

        var owner = await db.Set<WorkSourceLink>().AsNoTracking().Where(link => link.SourceKind == sourceKind && link.SourceId == id).Select(link => (long?)link.WorkId).FirstOrDefaultAsync(cancellationToken);
        return owner is null || owner == workId ? id : null;
    }

    /// <summary>
    /// After an import: the legacy record that received the files must belong to the request's Work. A record that belongs to no Work yet is linked
    /// to it (when its own provider id does not name something else) and enriched through the bridge; a record that belongs to another Work, or
    /// carries another provider id, is a conflict and is reported instead of silently switching the Work. Returns the reason of a conflict, or null.
    /// </summary>
    public async Task<string?> BindImportedAsync(AcquisitionRequest request, WorkSourceKind sourceKind, Guid legacyId, CancellationToken cancellationToken)
    {
        request = await EnsureBoundAsync(request, cancellationToken);
        if (request.WorkId is not { } workId)
        {
            return null;
        }

        var link = await db.Set<WorkSourceLink>().AsNoTracking().FirstOrDefaultAsync(item => item.SourceKind == sourceKind && item.SourceId == legacyId, cancellationToken);
        if (link is not null && link.WorkId != workId)
        {
            return "The files were imported into a library entry that belongs to a different title than the request. Review it before importing again.";
        }

        if (link is null)
        {
            var (provider, externalId) = await LegacyIdentityAsync(sourceKind, legacyId, cancellationToken);
            if (provider is not null && externalId is not null && provider.Equals(request.Provider, StringComparison.OrdinalIgnoreCase) && externalId != request.ExternalId)
            {
                return "The files were imported into a library entry that is matched to a different title than the request. Review it before importing again.";
            }

            await works.LinkSourceAsync(workId, sourceKind, legacyId, cancellationToken);
        }

        await EnrichAsync(request, sourceKind, legacyId, cancellationToken);
        return null;
    }

    private async Task EnrichAsync(AcquisitionRequest request, WorkSourceKind sourceKind, Guid legacyId, CancellationToken cancellationToken)
    {
        switch (sourceKind)
        {
            case WorkSourceKind.NovelWork:
                if (await db.NovelWorks.AsNoTracking().FirstOrDefaultAsync(novel => novel.Id == legacyId, cancellationToken) is { } novel)
                {
                    await bridge.EnsureWorkForNovelAsync(novel, MediaTypeOf(request.Kind), cancellationToken);
                }

                break;
            case WorkSourceKind.MangaSeries:
                await bridge.EnsureWorkForMangaSeriesAsync(legacyId, request.Title, request.Subtitle, request.Provider.Equals(NovelAniListProvider.ProviderKey, StringComparison.OrdinalIgnoreCase) ? request.ExternalId : null, cancellationToken);
                break;
        }
    }

    private async Task<long?> WorkOfIdentityAsync(WorkMediaType mediaType, string provider, string externalId, CancellationToken cancellationToken)
    {
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var normalizedId = externalId.Trim();
        var found = await db.Set<WorkExternalIdentity>().AsNoTracking()
            .Where(identity => identity.MediaType == mediaType && identity.Provider == normalizedProvider && identity.ExternalId == normalizedId)
            .Select(identity => (long?)identity.WorkId)
            .FirstOrDefaultAsync(cancellationToken);
        return found;
    }

    /// <summary>The Work of the legacy record that already holds the provider id (an AniList-matched series, a catalog-linked book), when that record is linked to one.</summary>
    private async Task<long?> WorkOfLegacyRecordAsync(MediaAcquisitionKind kind, string provider, string externalId, CancellationToken cancellationToken)
    {
        Guid? legacyId;
        WorkSourceKind sourceKind;
        if (kind == MediaAcquisitionKind.Manga)
        {
            sourceKind = WorkSourceKind.MangaSeries;
            legacyId = (await new MangaRepository(db).FindByMetadataAsync(provider, externalId, cancellationToken))?.Id;
        }
        else
        {
            sourceKind = WorkSourceKind.NovelWork;
            legacyId = await db.NovelWorks.AsNoTracking()
                .Where(novel => novel.MetadataProvider == provider && novel.MetadataExternalId == externalId && (novel.SourceProvider == BookCatalogService.ImportedBookProvider) == (kind == MediaAcquisitionKind.Book || kind == MediaAcquisitionKind.Audiobook))
                .Select(novel => (Guid?)novel.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (legacyId is not { } id)
        {
            return null;
        }

        return await db.Set<WorkSourceLink>().AsNoTracking()
            .Where(link => link.SourceKind == sourceKind && link.SourceId == id)
            .Select(link => (long?)link.WorkId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<(string? Provider, string? ExternalId)> LegacyIdentityAsync(WorkSourceKind sourceKind, Guid legacyId, CancellationToken cancellationToken)
    {
        if (sourceKind == WorkSourceKind.NovelWork)
        {
            var novel = await db.NovelWorks.AsNoTracking().Where(item => item.Id == legacyId).Select(item => new { item.MetadataProvider, item.MetadataExternalId }).FirstOrDefaultAsync(cancellationToken);
            return (novel?.MetadataProvider, novel?.MetadataExternalId);
        }

        var series = await db.Database
            .SqlQuery<SeriesIdentityRow>($"""SELECT "MetadataProvider", "MetadataExternalId" FROM "MangaSeries" WHERE "Id" = {legacyId.ToString()}""")
            .FirstOrDefaultAsync(cancellationToken);
        return (series?.MetadataProvider, series?.MetadataExternalId);
    }

    private sealed record SeriesIdentityRow(string? MetadataProvider, string? MetadataExternalId);
}
