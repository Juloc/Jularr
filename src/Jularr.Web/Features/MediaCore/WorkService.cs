using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Mapping;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.MediaCore;

/// <summary>What a <see cref="WorkService.MergeWorksAsync"/> moved from the absorbed work onto the survivor (#432).</summary>
public sealed record WorkMergeResult(
    Guid TargetWorkId,
    Guid SourceWorkId,
    int SourceLinks,
    int Identities,
    int Titles,
    int Relations,
    int Structure,
    int Provenance);

/// <summary>
/// The write surface of the universal media core (#592). Creates works, attaches normalized external
/// identities, titles, structure (seasons/episodes, volumes/chapters), editions/versions, typed
/// relations and field-level provenance — all provider-independent and idempotent. Provider mappings
/// are <b>correctable</b>: <see cref="ReassignExternalIdentityAsync"/> moves a provider id to a
/// different work and records the correction (reusing the #525 anime mapping audit where the work
/// bridges to an anime). No operation ever matches on a filename alone (#556).
/// </summary>
public sealed class WorkService(AppDbContext db)
{
    /// <summary>Creates a new work with a stable id and a cached canonical title.</summary>
    public async Task<Work> CreateWorkAsync(
        WorkMediaType mediaType,
        string canonicalTitle,
        int? year,
        CancellationToken cancellationToken)
    {
        var work = new Work
        {
            MediaType = mediaType,
            CanonicalTitle = canonicalTitle.Trim(),
            Year = year
        };
        db.Set<Work>().Add(work);
        await db.SaveChangesAsync(cancellationToken);
        return work;
    }

    /// <summary>
    /// Classifies a Movie or Series Work as Anime (or not). The evidence goes into the field provenance <c>classification.anime</c>, so a provider mapping never
    /// overrides an owner's decision; returns false when a stronger source already decided. Only the classification changes, never the Work's identity or structure.
    /// </summary>
    public async Task<bool> SetAnimeClassificationAsync(Guid workId, bool isAnime, string source, string? providerExternalId, bool isManualOverride, CancellationToken cancellationToken)
    {
        var work = await db.Set<Work>().FirstOrDefaultAsync(item => item.Id == workId && (item.MediaType == WorkMediaType.Movie || item.MediaType == WorkMediaType.Series), cancellationToken)
            ?? throw new InvalidOperationException("Only a Movie or Series Work can be classified as Anime.");
        if (!await SetFieldProvenanceAsync(workId, AnimeClassificationField, source, providerExternalId, 1.0, isManualOverride, MappingProviders.AniList, cancellationToken))
        {
            return false;
        }

        work.IsAnime = isAnime;
        work.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public const string AnimeClassificationField = "classification.anime";

    /// <summary>
    /// Resolves the work a provider identity already points at, or creates a new work and links the
    /// identity. Idempotent: the same (media type, provider, external id) always returns the same work.
    /// </summary>
    public async Task<Work> EnsureWorkByExternalIdentityAsync(
        WorkMediaType mediaType,
        string provider,
        string externalId,
        string title,
        int? year,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = MediaCoreNormalization.NormalizeProvider(provider);
        var normalizedExternalId = MediaCoreNormalization.NormalizeExternalId(externalId);
        if (normalizedProvider.Length == 0 || normalizedExternalId.Length == 0)
        {
            throw new ArgumentException("A provider and external id are required to resolve a work.");
        }

        var existing = await db.Set<WorkExternalIdentity>()
            .Where(x => x.MediaType == mediaType
                && x.Provider == normalizedProvider
                && x.ExternalId == normalizedExternalId)
            .Select(x => x.WorkId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != Guid.Empty)
        {
            return await db.Set<Work>().FirstAsync(x => x.Id == existing, cancellationToken);
        }

        var work = await CreateWorkAsync(mediaType, title, year, cancellationToken);
        await LinkExternalIdentityAsync(
            work.Id, mediaType, normalizedProvider, normalizedExternalId,
            confidence: 1.0, evidence: "provider id", isPrimary: true,
            isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        return work;
    }

    /// <summary>
    /// Upserts an external identity on a work. If the identity is already claimed by <b>another</b> work
    /// the mapping is left untouched (use <see cref="ReassignExternalIdentityAsync"/> to correct it),
    /// and this returns <c>false</c>. A manual identity is never downgraded by a non-manual upsert.
    /// </summary>
    public async Task<bool> LinkExternalIdentityAsync(
        Guid workId,
        WorkMediaType mediaType,
        string provider,
        string externalId,
        double confidence,
        string evidence,
        bool isPrimary,
        bool isManualOverride,
        MappingReviewState reviewState,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = MediaCoreNormalization.NormalizeProvider(provider);
        var normalizedExternalId = MediaCoreNormalization.NormalizeExternalId(externalId);

        var existing = await db.Set<WorkExternalIdentity>()
            .FirstOrDefaultAsync(
                x => x.MediaType == mediaType
                    && x.Provider == normalizedProvider
                    && x.ExternalId == normalizedExternalId,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.WorkId != workId)
            {
                return false; // claimed by another work; correction is an explicit operation
            }

            if (existing.IsManualOverride && !isManualOverride)
            {
                return false; // never downgrade an owner correction on refresh
            }

            existing.Confidence = confidence;
            existing.Evidence = evidence.Trim();
            existing.IsPrimary = isPrimary;
            existing.IsManualOverride = isManualOverride || existing.IsManualOverride;
            existing.ReviewState = reviewState;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (isPrimary)
        {
            await ClearPrimaryIdentityAsync(workId, mediaType, normalizedProvider, cancellationToken);
        }

        db.Set<WorkExternalIdentity>().Add(new WorkExternalIdentity
        {
            WorkId = workId,
            MediaType = mediaType,
            Provider = normalizedProvider,
            ExternalId = normalizedExternalId,
            Confidence = confidence,
            Evidence = evidence.Trim(),
            IsPrimary = isPrimary,
            IsManualOverride = isManualOverride,
            ReviewState = reviewState
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Correctable provider mapping (#592, reuses #525 intent): moves a provider identity to
    /// <paramref name="targetWorkId"/>, marking it a confirmed manual override so later provider
    /// refreshes cannot undo it. When the target work bridges to an anime, the correction is appended
    /// to the existing anime mapping audit trail. Returns the affected identity, or null if unknown.
    /// </summary>
    public async Task<WorkExternalIdentity?> ReassignExternalIdentityAsync(
        WorkMediaType mediaType,
        string provider,
        string externalId,
        Guid targetWorkId,
        string actor,
        string evidence,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = MediaCoreNormalization.NormalizeProvider(provider);
        var normalizedExternalId = MediaCoreNormalization.NormalizeExternalId(externalId);

        var identity = await db.Set<WorkExternalIdentity>()
            .FirstOrDefaultAsync(
                x => x.MediaType == mediaType
                    && x.Provider == normalizedProvider
                    && x.ExternalId == normalizedExternalId,
                cancellationToken);

        if (identity is null)
        {
            return null;
        }

        var previousWorkId = identity.WorkId;
        identity.WorkId = targetWorkId;
        identity.IsManualOverride = true;
        identity.ReviewState = MappingReviewState.Confirmed;
        identity.Evidence = string.IsNullOrWhiteSpace(evidence) ? "owner correction" : evidence.Trim();
        identity.UpdatedAt = DateTime.UtcNow;

        AppendIdentityChange(
            WorkIdentityChangeType.Reassign, mediaType, targetWorkId, previousWorkId,
            normalizedProvider, normalizedExternalId, actor,
            summary: $"Reassigned {normalizedProvider}:{normalizedExternalId}",
            details: $"from work {previousWorkId:D} to work {targetWorkId:D}; {identity.Evidence}");
        await db.SaveChangesAsync(cancellationToken);

        await TryAppendAnimeMappingAuditAsync(
            targetWorkId,
            action: MappingAuditStore.ActionApply,
            summary: $"Reassigned {normalizedProvider}:{normalizedExternalId}",
            details: $"from work {previousWorkId:D} to work {targetWorkId:D}; {identity.Evidence}",
            actor: actor,
            cancellationToken);

        return identity;
    }

    /// <summary>
    /// Manual <b>split</b> (#432): peels a provider identity off its current work into a brand-new work,
    /// marking it a confirmed manual override (a later provider refresh cannot undo it) and seeding the
    /// new work's primary title. Builds directly on the reassign primitive; returns the new work, or null
    /// when the identity is unknown.
    /// </summary>
    public async Task<Work?> SplitExternalIdentityToNewWorkAsync(
        WorkMediaType mediaType,
        string provider,
        string externalId,
        string newTitle,
        string actor,
        string evidence,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = MediaCoreNormalization.NormalizeProvider(provider);
        var normalizedExternalId = MediaCoreNormalization.NormalizeExternalId(externalId);

        var identity = await db.Set<WorkExternalIdentity>()
            .FirstOrDefaultAsync(
                x => x.MediaType == mediaType
                    && x.Provider == normalizedProvider
                    && x.ExternalId == normalizedExternalId,
                cancellationToken);
        if (identity is null)
        {
            return null;
        }

        var previousWorkId = identity.WorkId;
        var title = string.IsNullOrWhiteSpace(newTitle) ? normalizedExternalId : newTitle.Trim();
        var work = new Work { MediaType = mediaType, CanonicalTitle = title };
        db.Set<Work>().Add(work);

        identity.WorkId = work.Id;
        identity.IsPrimary = true;
        identity.IsManualOverride = true;
        identity.ReviewState = MappingReviewState.Confirmed;
        identity.Evidence = string.IsNullOrWhiteSpace(evidence) ? "split to new work" : evidence.Trim();
        identity.UpdatedAt = DateTime.UtcNow;

        db.Set<WorkTitle>().Add(new WorkTitle
        {
            WorkId = work.Id,
            TitleType = WorkTitleType.Primary,
            Language = "und",
            Value = title,
            NormalizedValue = MediaCoreNormalization.NormalizeTitle(title),
            IsPrimary = true,
            Source = MetadataFieldSources.Owner
        });

        AppendIdentityChange(
            WorkIdentityChangeType.Split, mediaType, work.Id, previousWorkId,
            normalizedProvider, normalizedExternalId, actor,
            summary: $"Split {normalizedProvider}:{normalizedExternalId} to a new work",
            details: $"from work {previousWorkId:D} to new work {work.Id:D}; {identity.Evidence}");
        await db.SaveChangesAsync(cancellationToken);
        return work;
    }

    /// <summary>
    /// Owner field-source pin (#435): records that a field's current value is a manual owner correction,
    /// so any later provider refresh is blocked from overwriting it by the precedence ladder. Returns
    /// true when the pin was applied (a manual override always wins).
    /// </summary>
    public Task<bool> SetManualFieldOverrideAsync(Guid workId, string fieldKey, CancellationToken cancellationToken) =>
        SetFieldProvenanceAsync(
            workId, fieldKey, MetadataFieldSources.Owner,
            providerExternalId: null, confidence: null, isManualOverride: true,
            preferredProvider: null, cancellationToken);

    /// <summary>
    /// Manual <b>merge</b> of two works (#432): absorbs <paramref name="sourceWorkId"/> into
    /// <paramref name="targetWorkId"/>, moving its bridged legacy records (<see cref="WorkSourceLink"/> —
    /// which is how progress, notes, wanted and collections stay attached), external identities, titles,
    /// relations, structure and field provenance onto the survivor, then removing the emptied work and
    /// recording a durable identity-change entry. Idempotent per pair: once the source is gone the merge
    /// cannot run again. Provenance and identity conflicts are resolved by the same precedence rules the
    /// rest of the core uses (manual overrides win; a provider identity never collides across works).
    /// </summary>
    public async Task<WorkMergeResult> MergeWorksAsync(
        Guid targetWorkId,
        Guid sourceWorkId,
        string actor,
        CancellationToken cancellationToken)
    {
        if (targetWorkId == sourceWorkId)
        {
            throw new ArgumentException("A work cannot be merged into itself.");
        }

        var target = await db.Set<Work>().FirstOrDefaultAsync(x => x.Id == targetWorkId, cancellationToken)
            ?? throw new InvalidOperationException($"Merge target work {targetWorkId:D} does not exist.");
        var source = await db.Set<Work>().FirstOrDefaultAsync(x => x.Id == sourceWorkId, cancellationToken)
            ?? throw new InvalidOperationException($"Merge source work {sourceWorkId:D} does not exist.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var sourceLinks = await MoveSourceLinksAsync(sourceWorkId, targetWorkId, cancellationToken);
        var identities = await MoveIdentitiesAsync(sourceWorkId, targetWorkId, cancellationToken);
        var titles = await MoveTitlesAsync(sourceWorkId, targetWorkId, cancellationToken);
        var relations = await MoveRelationsAsync(sourceWorkId, targetWorkId, cancellationToken);
        var structure = await MoveStructureAsync(sourceWorkId, targetWorkId, cancellationToken);
        var provenance = await MoveProvenanceAsync(sourceWorkId, targetWorkId, cancellationToken);
        await new WorkMetadataStore(db).MoveForMergeAsync(sourceWorkId, targetWorkId, cancellationToken);

        target.UpdatedAt = DateTime.UtcNow;

        var details =
            $"absorbed work {sourceWorkId:D} ({source.CanonicalTitle}); moved {sourceLinks} source links, "
            + $"{identities} identities, {titles} titles, {relations} relations, {structure} structure rows, "
            + $"{provenance} provenance rows.";
        AppendIdentityChange(
            WorkIdentityChangeType.Merge, target.MediaType, targetWorkId, sourceWorkId,
            provider: "", externalId: "", actor,
            summary: $"Merged \"{source.CanonicalTitle}\" into \"{target.CanonicalTitle}\"",
            details);
        await db.SaveChangesAsync(cancellationToken);
        await MoveMonitoringAsync(sourceWorkId, targetWorkId, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "WantedItems" WHERE "WorkId" = {sourceWorkId}""", cancellationToken);

        db.Set<Work>().Remove(source);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new WorkMergeResult(
            targetWorkId, sourceWorkId, sourceLinks, identities, titles, relations, structure, provenance);
    }

    /// <summary>
    /// The monitoring decisions follow the structure that moved: a decision of a node the survivor already had goes with that node, the others now belong to
    /// the survivor, and the absorbed Work's own decision becomes the survivor's unless the survivor decided for itself.
    /// </summary>
    private Task<int> MoveMonitoringAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "WorkMonitoring" m WHERE m."WorkId" = {sourceWorkId} AND m."TargetId" <> {sourceWorkId}
              AND NOT EXISTS (SELECT 1 FROM "WorkSeasons" x WHERE x."Id" = m."TargetId")
              AND NOT EXISTS (SELECT 1 FROM "WorkEpisodes" x WHERE x."Id" = m."TargetId")
              AND NOT EXISTS (SELECT 1 FROM "WorkVolumes" x WHERE x."Id" = m."TargetId")
              AND NOT EXISTS (SELECT 1 FROM "WorkChapters" x WHERE x."Id" = m."TargetId")
              AND NOT EXISTS (SELECT 1 FROM "WorkTracks" x WHERE x."Id" = m."TargetId");
            UPDATE "WorkMonitoring" SET "WorkId" = {targetWorkId} WHERE "WorkId" = {sourceWorkId} AND "TargetId" <> {sourceWorkId};
            DELETE FROM "WorkMonitoring" WHERE "TargetId" = {sourceWorkId} AND EXISTS (SELECT 1 FROM "WorkMonitoring" t WHERE t."TargetId" = {targetWorkId});
            UPDATE "WorkMonitoring" SET "TargetId" = {targetWorkId}, "WorkId" = {targetWorkId} WHERE "TargetId" = {sourceWorkId}
            """,
            cancellationToken);

    private async Task<int> MoveSourceLinksAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken)
    {
        // Each legacy record maps to exactly one work (unique on kind+id), so repointing never collides;
        // moving the bridge is what keeps the absorbed work's progress/notes/wanted/collections intact.
        var links = await db.Set<WorkSourceLink>().Where(x => x.WorkId == sourceWorkId).ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            link.WorkId = targetWorkId;
        }

        return links.Count;
    }

    private async Task<int> MoveIdentitiesAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken)
    {
        var targetPrimaries = (await db.Set<WorkExternalIdentity>()
            .Where(x => x.WorkId == targetWorkId && x.IsPrimary)
            .Select(x => new { x.Provider, x.MediaType })
            .ToListAsync(cancellationToken))
            .Select(x => (x.Provider, x.MediaType))
            .ToHashSet();

        // A given (provider, media type, external id) is globally unique, so no identity can collide
        // across the two works: every source identity simply moves to the survivor.
        var identities = await db.Set<WorkExternalIdentity>().Where(x => x.WorkId == sourceWorkId).ToListAsync(cancellationToken);
        foreach (var identity in identities)
        {
            identity.WorkId = targetWorkId;
            if (identity.IsPrimary && targetPrimaries.Contains((identity.Provider, identity.MediaType)))
            {
                identity.IsPrimary = false; // the survivor already has a primary for this provider namespace
            }

            identity.UpdatedAt = DateTime.UtcNow;
        }

        return identities.Count;
    }

    private async Task<int> MoveTitlesAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken)
    {
        var targetKeys = (await db.Set<WorkTitle>()
            .Where(x => x.WorkId == targetWorkId)
            .Select(x => new { x.TitleType, x.Language, x.NormalizedValue })
            .ToListAsync(cancellationToken))
            .Select(x => $"{(int)x.TitleType}\u0001{x.Language}\u0001{x.NormalizedValue}")
            .ToHashSet(StringComparer.Ordinal);

        var moved = 0;
        var titles = await db.Set<WorkTitle>().Where(x => x.WorkId == sourceWorkId).ToListAsync(cancellationToken);
        foreach (var title in titles)
        {
            var key = $"{(int)title.TitleType}\u0001{title.Language}\u0001{title.NormalizedValue}";
            if (!targetKeys.Add(key))
            {
                db.Set<WorkTitle>().Remove(title); // the survivor already has this exact title
                continue;
            }

            title.WorkId = targetWorkId;
            title.IsPrimary = false; // the survivor keeps its own primary title
            moved++;
        }

        return moved;
    }

    private async Task<int> MoveRelationsAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken)
    {
        var targetKeys = (await db.Set<WorkRelation>()
            .Where(x => x.FromWorkId == targetWorkId || x.ToWorkId == targetWorkId)
            .Select(x => new { x.FromWorkId, x.ToWorkId, x.RelationType })
            .ToListAsync(cancellationToken))
            .Select(x => $"{x.FromWorkId:N}\u0001{x.ToWorkId:N}\u0001{(int)x.RelationType}")
            .ToHashSet(StringComparer.Ordinal);

        var moved = 0;
        var relations = await db.Set<WorkRelation>()
            .Where(x => x.FromWorkId == sourceWorkId || x.ToWorkId == sourceWorkId)
            .ToListAsync(cancellationToken);
        foreach (var relation in relations)
        {
            var from = relation.FromWorkId == sourceWorkId ? targetWorkId : relation.FromWorkId;
            var to = relation.ToWorkId == sourceWorkId ? targetWorkId : relation.ToWorkId;
            if (from == to)
            {
                db.Set<WorkRelation>().Remove(relation); // a relation between the two merged works self-annihilates
                continue;
            }

            var key = $"{from:N}\u0001{to:N}\u0001{(int)relation.RelationType}";
            if (!targetKeys.Add(key))
            {
                db.Set<WorkRelation>().Remove(relation); // the survivor already has this edge
                continue;
            }

            relation.FromWorkId = from;
            relation.ToWorkId = to;
            moved++;
        }

        return moved;
    }

    private async Task<int> MoveStructureAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken)
    {
        var moved = 0;
        moved += await RepointOrDropAsync<WorkSeason>(
            sourceWorkId, targetWorkId, x => x.SeasonNumber.ToString(CultureInfo.InvariantCulture), cancellationToken);
        moved += await RepointOrDropAsync<WorkEpisode>(
            sourceWorkId, targetWorkId, x => $"{x.SeasonNumber}\u0001{x.EpisodeNumber}", cancellationToken);
        moved += await RepointOrDropAsync<WorkVolume>(
            sourceWorkId, targetWorkId, x => x.Number.ToString(CultureInfo.InvariantCulture), cancellationToken);
        moved += await RepointOrDropAsync<WorkChapter>(
            sourceWorkId, targetWorkId, x => x.Number.ToString(CultureInfo.InvariantCulture), cancellationToken);
        moved += await RepointOrDropAsync<WorkEdition>(
            sourceWorkId, targetWorkId, x => x.EditionKey, cancellationToken);
        moved += await RepointOrDropAsync<WorkVersion>(
            sourceWorkId, targetWorkId, x => x.VersionKey, cancellationToken);
        return moved;
    }

    /// <summary>
    /// Repoints a work-owned child table (every one has a <c>WorkId</c>) to the survivor, dropping any
    /// source row whose per-work unique key already exists on the survivor so the unique index holds.
    /// </summary>
    private async Task<int> RepointOrDropAsync<T>(
        Guid sourceWorkId,
        Guid targetWorkId,
        Func<T, string> uniqueKey,
        CancellationToken cancellationToken)
        where T : class
    {
        var set = db.Set<T>();
        var targetKeys = (await set
            .Where(x => EF.Property<Guid>(x, "WorkId") == targetWorkId)
            .ToListAsync(cancellationToken))
            .Select(uniqueKey)
            .ToHashSet(StringComparer.Ordinal);

        var sourceRows = await set
            .Where(x => EF.Property<Guid>(x, "WorkId") == sourceWorkId)
            .ToListAsync(cancellationToken);
        var workIdProperty = typeof(T).GetProperty("WorkId")!;

        var moved = 0;
        foreach (var row in sourceRows)
        {
            if (!targetKeys.Add(uniqueKey(row)))
            {
                set.Remove(row); // the survivor already has this row
                continue;
            }

            workIdProperty.SetValue(row, targetWorkId);
            moved++;
        }

        return moved;
    }

    private async Task<int> MoveProvenanceAsync(Guid sourceWorkId, Guid targetWorkId, CancellationToken cancellationToken)
    {
        var targetByField = (await db.Set<WorkFieldProvenance>()
            .Where(x => x.WorkId == targetWorkId)
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.FieldKey, StringComparer.Ordinal);

        var moved = 0;
        var sourceRows = await db.Set<WorkFieldProvenance>().Where(x => x.WorkId == sourceWorkId).ToListAsync(cancellationToken);
        foreach (var row in sourceRows)
        {
            if (targetByField.TryGetValue(row.FieldKey, out var existing))
            {
                if (MetadataFieldSources.ShouldReplace(existing, row.Source, row.IsManualOverride))
                {
                    existing.Source = row.Source;
                    existing.ProviderExternalId = row.ProviderExternalId;
                    existing.Confidence = row.Confidence;
                    existing.IsManualOverride = row.IsManualOverride;
                    existing.FallbackPriority = row.FallbackPriority;
                    existing.FetchedAt = row.FetchedAt;
                    existing.UpdatedAt = DateTime.UtcNow;
                }

                db.Set<WorkFieldProvenance>().Remove(row);
                continue;
            }

            row.WorkId = targetWorkId;
            targetByField[row.FieldKey] = row;
            moved++;
        }

        return moved;
    }

    private void AppendIdentityChange(
        WorkIdentityChangeType changeType,
        WorkMediaType mediaType,
        Guid targetWorkId,
        Guid? sourceWorkId,
        string provider,
        string externalId,
        string actor,
        string summary,
        string details) =>
        db.Set<WorkIdentityChange>().Add(new WorkIdentityChange
        {
            ChangeType = changeType,
            MediaType = mediaType,
            TargetWorkId = targetWorkId,
            SourceWorkId = sourceWorkId,
            Provider = provider,
            ExternalId = externalId,
            Actor = (actor ?? "").Trim(),
            Summary = summary,
            Details = details
        });

    /// <summary>Sets the review state of an external identity (confirm / flag / reject).</summary>
    public async Task<bool> SetExternalIdentityReviewStateAsync(
        Guid identityId,
        MappingReviewState reviewState,
        CancellationToken cancellationToken)
    {
        var identity = await db.Set<WorkExternalIdentity>()
            .FirstOrDefaultAsync(x => x.Id == identityId, cancellationToken);
        if (identity is null)
        {
            return false;
        }

        identity.ReviewState = reviewState;
        identity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Adds or refreshes a title, de-duplicated by (work, type, language, normalized value).</summary>
    public async Task<WorkTitle> AddOrUpdateTitleAsync(
        Guid workId,
        WorkTitleType titleType,
        string language,
        string value,
        string source,
        bool isPrimary,
        CancellationToken cancellationToken)
    {
        var normalizedLanguage = (language ?? "und").Trim().ToLowerInvariant();
        var normalizedValue = MediaCoreNormalization.NormalizeTitle(value);

        var existing = await db.Set<WorkTitle>()
            .FirstOrDefaultAsync(
                x => x.WorkId == workId
                    && x.TitleType == titleType
                    && x.Language == normalizedLanguage
                    && x.NormalizedValue == normalizedValue,
                cancellationToken);

        if (isPrimary)
        {
            await ClearPrimaryTitleAsync(workId, cancellationToken);
        }

        if (existing is not null)
        {
            existing.Value = value.Trim();
            existing.Source = MediaCoreNormalization.NormalizeProvider(source);
            existing.IsPrimary = isPrimary || existing.IsPrimary;
            await PersistPrimaryTitleCacheAsync(workId, existing, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var title = new WorkTitle
        {
            WorkId = workId,
            TitleType = titleType,
            Language = normalizedLanguage,
            Value = value.Trim(),
            NormalizedValue = normalizedValue,
            Source = MediaCoreNormalization.NormalizeProvider(source),
            IsPrimary = isPrimary
        };
        db.Set<WorkTitle>().Add(title);
        await PersistPrimaryTitleCacheAsync(workId, title, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return title;
    }

    /// <summary>Upserts a typed relation edge, unique per (from, to, type). Optionally writes the inverse edge too.</summary>
    public async Task AddRelationAsync(
        Guid fromWorkId,
        Guid toWorkId,
        WorkRelationType relationType,
        string source,
        bool isManualOverride,
        bool includeInverse,
        CancellationToken cancellationToken)
    {
        if (fromWorkId == toWorkId)
        {
            throw new ArgumentException("A work cannot relate to itself.");
        }

        await UpsertRelationAsync(fromWorkId, toWorkId, relationType, source, isManualOverride, cancellationToken);
        if (includeInverse)
        {
            await UpsertRelationAsync(
                toWorkId, fromWorkId, WorkRelationTypes.Inverse(relationType), source, isManualOverride, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records where a field's value came from, applying the #435 precedence ladder. Returns true when
    /// the incoming source won (a manual override is never overwritten by a provider refresh).
    /// </summary>
    public async Task<bool> SetFieldProvenanceAsync(
        Guid workId,
        string fieldKey,
        string source,
        string? providerExternalId,
        double? confidence,
        bool isManualOverride,
        string? preferredProvider,
        CancellationToken cancellationToken)
    {
        var normalizedField = (fieldKey ?? "").Trim();
        var normalizedSource = MetadataFieldSources.Normalize(source);
        var existing = await db.Set<WorkFieldProvenance>()
            .FirstOrDefaultAsync(x => x.WorkId == workId && x.FieldKey == normalizedField, cancellationToken);

        if (!MetadataFieldSources.ShouldReplace(existing, normalizedSource, isManualOverride, preferredProvider))
        {
            return false;
        }

        var priority = MetadataFieldSources.PriorityFor(normalizedSource, isManualOverride, preferredProvider);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            db.Set<WorkFieldProvenance>().Add(new WorkFieldProvenance
            {
                WorkId = workId,
                FieldKey = normalizedField,
                Source = normalizedSource,
                ProviderExternalId = providerExternalId?.Trim(),
                Confidence = confidence,
                IsManualOverride = isManualOverride,
                FallbackPriority = priority,
                FetchedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Source = normalizedSource;
            existing.ProviderExternalId = providerExternalId?.Trim();
            existing.Confidence = confidence;
            existing.IsManualOverride = isManualOverride;
            existing.FallbackPriority = priority;
            existing.FetchedAt = now;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Bridges a work to an existing per-type record (adapter). Each legacy record maps to one work, and a link is never moved silently: asking to link a record
    /// that already belongs to another work is a conflict (<see cref="WorkSourceLinkConflictException"/>) that the caller must surface for review. Moving records
    /// between works is the explicit owner action <see cref="MergeWorksAsync"/>.
    /// </summary>
    public async Task<WorkSourceLink> LinkSourceAsync(
        Guid workId,
        WorkSourceKind sourceKind,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<WorkSourceLink>()
            .FirstOrDefaultAsync(x => x.SourceKind == sourceKind && x.SourceId == sourceId, cancellationToken);

        if (existing is not null)
        {
            return existing.WorkId == workId ? existing : throw new WorkSourceLinkConflictException(sourceKind, sourceId, existing.WorkId, workId);
        }

        var link = new WorkSourceLink { WorkId = workId, SourceKind = sourceKind, SourceId = sourceId };
        db.Set<WorkSourceLink>().Add(link);
        await db.SaveChangesAsync(cancellationToken);
        return link;
    }

    private async Task UpsertRelationAsync(
        Guid fromWorkId,
        Guid toWorkId,
        WorkRelationType relationType,
        string source,
        bool isManualOverride,
        CancellationToken cancellationToken)
    {
        var existing = await db.Set<WorkRelation>()
            .FirstOrDefaultAsync(
                x => x.FromWorkId == fromWorkId && x.ToWorkId == toWorkId && x.RelationType == relationType,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.IsManualOverride && !isManualOverride)
            {
                return; // provider refresh cannot override a manual edge
            }

            existing.Source = MediaCoreNormalization.NormalizeProvider(source);
            existing.IsManualOverride = isManualOverride || existing.IsManualOverride;
            return;
        }

        db.Set<WorkRelation>().Add(new WorkRelation
        {
            FromWorkId = fromWorkId,
            ToWorkId = toWorkId,
            RelationType = relationType,
            Source = MediaCoreNormalization.NormalizeProvider(source),
            IsManualOverride = isManualOverride
        });
    }

    private async Task ClearPrimaryIdentityAsync(
        Guid workId,
        WorkMediaType mediaType,
        string provider,
        CancellationToken cancellationToken)
    {
        var current = await db.Set<WorkExternalIdentity>()
            .Where(x => x.WorkId == workId && x.MediaType == mediaType && x.Provider == provider && x.IsPrimary)
            .ToListAsync(cancellationToken);
        foreach (var identity in current)
        {
            identity.IsPrimary = false;
        }
    }

    private async Task ClearPrimaryTitleAsync(Guid workId, CancellationToken cancellationToken)
    {
        var current = await db.Set<WorkTitle>()
            .Where(x => x.WorkId == workId && x.IsPrimary)
            .ToListAsync(cancellationToken);
        foreach (var title in current)
        {
            title.IsPrimary = false;
        }
    }

    private async Task PersistPrimaryTitleCacheAsync(Guid workId, WorkTitle title, CancellationToken cancellationToken)
    {
        if (!title.IsPrimary)
        {
            return;
        }

        var work = await db.Set<Work>().FirstOrDefaultAsync(x => x.Id == workId, cancellationToken);
        if (work is not null)
        {
            work.CanonicalTitle = title.Value;
            work.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task TryAppendAnimeMappingAuditAsync(
        Guid workId,
        string action,
        string summary,
        string details,
        string actor,
        CancellationToken cancellationToken)
    {
        var animeSourceId = await db.Set<WorkSourceLink>()
            .Where(x => x.WorkId == workId && x.SourceKind == WorkSourceKind.Anime)
            .Select(x => (Guid?)x.SourceId)
            .FirstOrDefaultAsync(cancellationToken);

        if (animeSourceId is { } animeId)
        {
            await new MappingAuditStore(db).AppendAsync(animeId, action, summary, details, actor, cancellationToken);
        }
    }
}
