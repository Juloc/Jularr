using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>Why a library entry is not bound to a Work without the owner (#432).</summary>
public enum LibraryWorkReviewReason
{
    /// <summary>The entry carries no provider id that names a Work; the title is never evidence.</summary>
    NoProviderIdentity,

    /// <summary>The ids of the entry point at two different Works.</summary>
    ConflictingIdentities,

    /// <summary>The Work the ids name already stands for another library entry, so binding this one would make the Work ambiguous (the database already refuses two entries with the same metadata id).</summary>
    WorkStandsForAnotherEntry
}

public enum LibraryWorkResolution
{
    Linked,
    Created,
    NotFound,
    AlreadyBound,
    WrongMediaType,
    IdentityHeldByAnotherWork,
    WorkRepresentsAnotherEntry
}

public sealed record LibraryWorkCandidate(Guid WorkId, string Title, string Evidence);

public sealed record LibraryWorkReviewItem(
    Guid EntryId,
    WorkMediaType MediaType,
    string Title,
    string? Author,
    IReadOnlyList<string> Identities,
    LibraryWorkReviewReason Reason,
    IReadOnlyList<LibraryWorkCandidate> Candidates);

public sealed record LibraryWorkBackfillResult(int Bound, int Created, int Review, int Failed);

/// <summary>A provider id of a library entry that is a stable identity of a Work.</summary>
public sealed record ProviderId(string Provider, string ExternalId);

/// <summary>
/// Gives the durable Book and Light Novel library entries (<see cref="NovelWork"/>) their canonical Work (#432). It reads the same evidence the
/// request binder reads, only the other way round: a provider id that names a Work (an AniList or Syosetu id for a Light Novel, a catalog id for
/// a Book) binds the entry to the Work that id already identifies, or creates the one Work that id identifies; the title is never evidence. An entry
/// whose evidence is missing, points at two Works, or is shared with another entry is left as it is and listed for the owner, who resolves it
/// explicitly. A source link is never moved: the only ways to change a Work are the owner's link or create here and the canonical merge.
/// Idempotent and restart-safe: what is bound is skipped, and nothing is remembered besides the links themselves.
/// </summary>
public sealed class LibraryWorkBackfill(AppDbContext db, WorkService works, LegacyWorkBridge bridge, ILogger<LibraryWorkBackfill> logger)
{
    public const int BatchSize = 200;
    public const int MaxRecordsPerPass = 5000;

    private enum Decision
    {
        Bind,
        Create,
        Review
    }

    private sealed record Evaluation(Decision Decision, WorkMediaType MediaType, IReadOnlyList<ProviderId> Identities, Guid? WorkId, LibraryWorkReviewReason? Reason, IReadOnlyList<LibraryWorkCandidate> Candidates);

    public static WorkMediaType MediaTypeOf(NovelWork entry) => entry.SourceProvider == BookCatalogService.ImportedBookProvider ? WorkMediaType.Book : WorkMediaType.LightNovel;

    /// <summary>One bounded pass over the entries without a Work: binds what is certain and leaves the rest untouched.</summary>
    public async Task<LibraryWorkBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        int bound = 0, created = 0, review = 0, failed = 0, scanned = 0;
        Guid? after = null;
        while (scanned < MaxRecordsPerPass)
        {
            var batch = await Unbound().Where(entry => after == null || entry.Id.CompareTo(after.Value) > 0).OrderBy(entry => entry.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entry in batch)
            {
                try
                {
                    var evaluation = await EvaluateAsync(entry, cancellationToken);
                    switch (evaluation.Decision)
                    {
                        case Decision.Bind:
                            await BindAsync(entry, evaluation.MediaType, evaluation.WorkId!.Value, cancellationToken);
                            bound++;
                            break;
                        case Decision.Create:
                            var primary = evaluation.Identities[0];
                            var work = await works.EnsureWorkByExternalIdentityAsync(evaluation.MediaType, primary.Provider, primary.ExternalId, entry.Title, null, cancellationToken);
                            await BindAsync(entry, evaluation.MediaType, work.Id, cancellationToken);
                            created++;
                            break;
                        default:
                            review++;
                            break;
                    }
                }
                catch (WorkSourceLinkConflictException exception)
                {
                    logger.LogWarning(exception, "Library entry {Record} was bound to another Work while the backfill ran; left for the review.", entry.Id);
                    review++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Library entry {Record} could not be bound to a Work; the next pass tries again.", entry.Id);
                    failed++;
                }
            }

            after = batch[^1].Id;
            scanned += batch.Count;
        }

        if (bound + created + failed > 0)
        {
            logger.LogInformation("Library Work backfill: {Bound} bound, {Created} created, {Review} left for review, {Failed} failed.", bound, created, review, failed);
        }

        return new LibraryWorkBackfillResult(bound, created, review, failed);
    }

    /// <summary>The entries the owner has to decide on, with the Works that could be meant. Computed from the current links, so a resolved entry leaves the list by itself.</summary>
    public async Task<IReadOnlyList<LibraryWorkReviewItem>> ListReviewAsync(int limit, CancellationToken cancellationToken)
    {
        var items = new List<LibraryWorkReviewItem>();
        Guid? after = null;
        var scanned = 0;
        while (items.Count < limit && scanned < MaxRecordsPerPass)
        {
            var batch = await Unbound().Where(entry => after == null || entry.Id.CompareTo(after.Value) > 0).OrderBy(entry => entry.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entry in batch.TakeWhile(_ => items.Count < limit))
            {
                var evaluation = await EvaluateAsync(entry, cancellationToken);
                if (evaluation.Decision == Decision.Review)
                {
                    var identities = evaluation.Identities.Select(identity => $"{identity.Provider}:{identity.ExternalId}").ToArray();
                    items.Add(new LibraryWorkReviewItem(entry.Id, evaluation.MediaType, entry.Title, entry.Author, identities, evaluation.Reason!.Value, evaluation.Candidates));
                }
            }

            after = batch[^1].Id;
            scanned += batch.Count;
        }

        return items;
    }

    /// <summary>The owner's decision to link an entry to a Work that exists: refused when it would leave an id of the entry with another Work, or make the Work stand for two entries.</summary>
    public async Task<LibraryWorkResolution> LinkToWorkAsync(Guid entryId, Guid workId, CancellationToken cancellationToken)
    {
        var entry = await db.NovelWorks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        var work = await db.Works.AsNoTracking().FirstOrDefaultAsync(item => item.Id == workId, cancellationToken);
        if (entry is null || work is null)
        {
            return LibraryWorkResolution.NotFound;
        }

        var mediaType = MediaTypeOf(entry);
        if (await IsBoundAsync(entryId, cancellationToken))
        {
            return LibraryWorkResolution.AlreadyBound;
        }

        if (work.MediaType != mediaType)
        {
            return LibraryWorkResolution.WrongMediaType;
        }

        foreach (var identity in IdentitiesOf(entry))
        {
            if (await HolderOfAsync(mediaType, identity, cancellationToken) is { } holder && holder != workId)
            {
                return LibraryWorkResolution.IdentityHeldByAnotherWork;
            }
        }

        if (await RepresentsAnotherEntryAsync(workId, entryId, cancellationToken))
        {
            return LibraryWorkResolution.WorkRepresentsAnotherEntry;
        }

        await BindAsync(entry, mediaType, workId, cancellationToken);
        return LibraryWorkResolution.Linked;
    }

    /// <summary>The owner's decision that an entry is a Work of its own: refused when an id of the entry already names a Work, because then the entry belongs to that Work or the Works are merged.</summary>
    public async Task<(LibraryWorkResolution Resolution, Guid? WorkId)> CreateWorkAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await db.NovelWorks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return (LibraryWorkResolution.NotFound, null);
        }

        if (await IsBoundAsync(entryId, cancellationToken))
        {
            return (LibraryWorkResolution.AlreadyBound, null);
        }

        var mediaType = MediaTypeOf(entry);
        foreach (var identity in IdentitiesOf(entry))
        {
            if (await HolderOfAsync(mediaType, identity, cancellationToken) is not null)
            {
                return (LibraryWorkResolution.IdentityHeldByAnotherWork, null);
            }
        }

        var work = await works.CreateWorkAsync(mediaType, entry.Title, null, cancellationToken);
        await BindAsync(entry, mediaType, work.Id, cancellationToken);
        return (LibraryWorkResolution.Created, work.Id);
    }

    private IQueryable<NovelWork> Unbound() => db.NovelWorks.AsNoTracking().Where(entry => !db.WorkSourceLinks.Any(link => link.SourceKind == WorkSourceKind.NovelWork && link.SourceId == entry.Id));

    private Task<bool> IsBoundAsync(Guid entryId, CancellationToken cancellationToken) =>
        db.WorkSourceLinks.AnyAsync(link => link.SourceKind == WorkSourceKind.NovelWork && link.SourceId == entryId, cancellationToken);

    private Task<bool> RepresentsAnotherEntryAsync(Guid workId, Guid entryId, CancellationToken cancellationToken) =>
        db.WorkSourceLinks.AnyAsync(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.NovelWork && link.SourceId != entryId, cancellationToken);

    /// <summary>Links the entry to the Work and lets the bridge mirror its titles and ids; an id the Work does not hold yet is added, one that another Work holds stays where it is.</summary>
    private async Task BindAsync(NovelWork entry, WorkMediaType mediaType, Guid workId, CancellationToken cancellationToken)
    {
        await works.LinkSourceAsync(workId, WorkSourceKind.NovelWork, entry.Id, cancellationToken);
        await bridge.EnsureWorkForNovelAsync(entry, mediaType, cancellationToken);
    }

    /// <summary>The provider ids of an entry that are stable identities of a Work: the metadata id and, for a web novel, its source key.</summary>
    private static List<ProviderId> IdentitiesOf(NovelWork entry)
    {
        var kind = MediaTypeOf(entry) == WorkMediaType.Book ? MediaAcquisitionKind.Book : MediaAcquisitionKind.LightNovel;
        var identities = new List<ProviderId>();
        if (!string.IsNullOrWhiteSpace(entry.MetadataProvider) && !string.IsNullOrWhiteSpace(entry.MetadataExternalId) && RequestWorkBinder.IsTrustworthy(kind, entry.MetadataProvider, entry.MetadataExternalId))
        {
            identities.Add(new ProviderId(entry.MetadataProvider, entry.MetadataExternalId));
        }

        if (!string.IsNullOrWhiteSpace(entry.SourceProvider) && !string.IsNullOrWhiteSpace(entry.SourceKey) && RequestWorkBinder.IsTrustworthy(kind, entry.SourceProvider, entry.SourceKey))
        {
            identities.Add(new ProviderId(entry.SourceProvider, entry.SourceKey));
        }

        return identities;
    }

    private async Task<Guid?> HolderOfAsync(WorkMediaType mediaType, ProviderId identity, CancellationToken cancellationToken)
    {
        var provider = MediaCoreNormalization.NormalizeProvider(identity.Provider);
        var externalId = MediaCoreNormalization.NormalizeExternalId(identity.ExternalId);
        return await db.WorkExternalIdentities.AsNoTracking()
            .Where(item => item.MediaType == mediaType && item.Provider == provider && item.ExternalId == externalId)
            .Select(item => (Guid?)item.WorkId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Evaluation> EvaluateAsync(NovelWork entry, CancellationToken cancellationToken)
    {
        var mediaType = MediaTypeOf(entry);
        var identities = IdentitiesOf(entry);
        if (identities.Count == 0)
        {
            return new Evaluation(Decision.Review, mediaType, identities, null, LibraryWorkReviewReason.NoProviderIdentity, []);
        }

        var evidence = new Dictionary<Guid, string>();
        foreach (var identity in identities)
        {
            if (await HolderOfAsync(mediaType, identity, cancellationToken) is { } holder)
            {
                evidence.TryAdd(holder, $"{identity.Provider}:{identity.ExternalId}");
            }
        }

        if (evidence.Count > 1)
        {
            return new Evaluation(Decision.Review, mediaType, identities, null, LibraryWorkReviewReason.ConflictingIdentities, await CandidatesAsync(evidence, cancellationToken));
        }

        if (evidence.Count == 0)
        {
            return new Evaluation(Decision.Create, mediaType, identities, null, null, []);
        }

        var workId = evidence.Keys.Single();
        if (await RepresentsAnotherEntryAsync(workId, entry.Id, cancellationToken))
        {
            return new Evaluation(Decision.Review, mediaType, identities, null, LibraryWorkReviewReason.WorkStandsForAnotherEntry, await CandidatesAsync(evidence, cancellationToken));
        }

        return new Evaluation(Decision.Bind, mediaType, identities, workId, null, []);
    }

    private async Task<IReadOnlyList<LibraryWorkCandidate>> CandidatesAsync(Dictionary<Guid, string> evidence, CancellationToken cancellationToken)
    {
        var ids = evidence.Keys.ToArray();
        var titles = await db.Works.AsNoTracking().Where(work => ids.Contains(work.Id)).ToDictionaryAsync(work => work.Id, work => work.CanonicalTitle, cancellationToken);
        return ids.Select(id => new LibraryWorkCandidate(id, titles.GetValueOrDefault(id, ""), evidence[id])).OrderBy(candidate => candidate.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
