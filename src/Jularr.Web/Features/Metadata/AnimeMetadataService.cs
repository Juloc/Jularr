using Jularr.Web.Data;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Mapping;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Metadata;

public sealed class AnimeMetadataService(
    AppDbContext db,
    IEnumerable<IAnimeMetadataProvider> providers,
    AniListAccountStore aniListStore,
    MediaMappingReviewStore reviewStore,
    WorkService? works = null)
{
    /// <summary>
    /// Mirrors the AniList match of a library anime onto its canonical Work: the entry is the Work's AniList identity only while it covers the whole title. Once episode
    /// ranges map other entries to parts of it, the ranges (the structural mapping evidence) carry the entries and no entry stands for the whole Work. The AniList
    /// match is also the classification evidence that the Work is Anime. Idempotent; an owner's identity or classification decision is never overridden.
    /// </summary>
    public async Task SyncWorkIdentityAsync(Guid animeId, CancellationToken cancellationToken)
    {
        if (works is null)
        {
            return;
        }

        var match = await db.AnimeMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.AnimeId == animeId && x.Provider == AniListMetadataProvider.ProviderKey, cancellationToken);
        var workId = await db.WorkSourceLinks.AsNoTracking().Where(link => link.SourceKind == WorkSourceKind.Anime && link.SourceId == animeId).Select(link => (Guid?)link.WorkId).FirstOrDefaultAsync(cancellationToken);
        if (match is null || workId is null)
        {
            return;
        }

        if ((await aniListStore.LoadEpisodeMappingsAsync(animeId, cancellationToken)).Count > 0)
        {
            await db.WorkExternalIdentities
                .Where(identity => identity.WorkId == workId && identity.MediaType == WorkMediaType.Anime && identity.Provider == match.Provider && identity.ExternalId == match.ExternalId && !identity.IsManualOverride)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            await works.LinkExternalIdentityAsync(workId.Value, WorkMediaType.Anime, match.Provider, match.ExternalId, 1.0, "AniList match of the whole title", isPrimary: true, isManualOverride: false, MappingReviewState.Confirmed, cancellationToken);
        }

        await works.SetAnimeClassificationAsync(workId.Value, true, match.Provider, match.ExternalId, isManualOverride: false, cancellationToken);
    }

    // Raw-ADO.NET derived-state store (see ProviderRoleAssignmentStore); not DI-registered, so it
    // is constructed inline from the already-injected db context, same as MappingReviewModel does.
    private ProviderRoleAssignmentStore Roles() => new(db);

    public Task<AnimeMetadata?> GetAsync(
        Guid animeId,
        CancellationToken cancellationToken) =>
        db.AnimeMetadata
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.AnimeId == animeId, cancellationToken);


    public Task<IReadOnlyList<AnimeEpisodeMetadataMapping>> GetEpisodeMappingsAsync(
        Guid animeId,
        CancellationToken cancellationToken) =>
        aniListStore.LoadEpisodeMappingsAsync(animeId, cancellationToken);

    public async Task<AutomaticAnimeEpisodeMappingResult> AutoMapEpisodeRangesAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var existingMappings = await aniListStore.LoadEpisodeMappingsAsync(
            animeId,
            cancellationToken);
        if (existingMappings.Count > 0)
        {
            return AutomaticAnimeEpisodeMappingResult.Skipped(
                "Explicit episode mappings already exist; automatic mapping will not replace them.");
        }

        // Issue #568: episode-range auto-mapping only ever produces AniList-numbered ranges, so it
        // only runs while this anime's resolved EpisodeStructure role is AniList (the built-in
        // default, reproducing today's behaviour) or has not been explicitly assigned away from it.
        // An explicit non-AniList assignment (support for non-AniList episode numbering) opts the
        // anime out, leaving its local numbering untouched.
        var structureRole = await Roles().ResolveRoleForWorkAsync(
            animeId,
            MappingProviderRole.EpisodeStructure,
            cancellationToken);
        if (structureRole.Source != ProviderRoleSource.BuiltIn &&
            !string.Equals(structureRole.Provider, MappingProviders.AniList, StringComparison.OrdinalIgnoreCase))
        {
            return AutomaticAnimeEpisodeMappingResult.Skipped(
                $"Episode structure for this anime is configured to use '{structureRole.Provider}', not AniList; automatic episode-range mapping is skipped.");
        }

        var localTitle = await db.Anime
            .AsNoTracking()
            .Where(x => x.Id == animeId)
            .Select(x => x.Title)
            .SingleOrDefaultAsync(cancellationToken)
            ?? animeId.ToString();

        var metadata = await GetAsync(animeId, cancellationToken);
        if (metadata is null)
        {
            await AutoMatchAsync(animeId, cancellationToken);
            metadata = await GetAsync(animeId, cancellationToken);
        }

        if (metadata is null ||
            !string.Equals(
                metadata.Provider,
                AniListMetadataProvider.ProviderKey,
                StringComparison.OrdinalIgnoreCase))
        {
            await reviewStore.UpsertAsync(
                "anime",
                animeId.ToString(),
                localTitle,
                "episode-ranges",
                "The local anime does not have a safe AniList metadata match.",
                [],
                cancellationToken);

            return AutomaticAnimeEpisodeMappingResult.Skipped(
                "The local anime does not have a safe AniList metadata match.");
        }

        var aniListProvider = providers
            .OfType<AniListMetadataProvider>()
            .FirstOrDefault();
        if (aniListProvider is null)
        {
            return AutomaticAnimeEpisodeMappingResult.Skipped(
                "The AniList metadata provider is not available.");
        }

        var localEpisodes = await db.Episodes
            .AsNoTracking()
            .Where(x => x.AnimeId == animeId && x.Number > 0)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.Number)
            .Select(x => new LocalEpisodeCoordinate(
                x.SeasonNumber,
                x.Number))
            .ToListAsync(cancellationToken);

        var regularEpisodes = localEpisodes
            .Where(x => x.SeasonNumber > 0)
            .ToArray();
        var specialEpisodes = localEpisodes
            .Where(x => x.SeasonNumber == 0)
            .ToArray();

        var plannedRanges = new List<PlannedAnimeEpisodeRange>();
        var reviewCandidates = new List<MediaMappingReviewCandidate>();

        if (regularEpisodes.Length > 0)
        {
            IReadOnlyList<AnimeMetadataCandidate> sequence;
            try
            {
                sequence = await aniListProvider.GetLinearSequenceAsync(
                    metadata.ExternalId,
                    cancellationToken);
            }
            catch (MetadataProviderException exception)
            {
                await reviewStore.UpsertAsync(
                    "anime",
                    animeId.ToString(),
                    localTitle,
                    "episode-ranges",
                    exception.Message,
                    [],
                    cancellationToken);

                return AutomaticAnimeEpisodeMappingResult.Skipped(
                    exception.Message);
            }

            if (sequence.Any(x => x.EpisodeCount is not > 0))
            {
                const string reason =
                    "AniList does not expose reliable episode counts for every related part.";
                await reviewStore.UpsertAsync(
                    "anime",
                    animeId.ToString(),
                    localTitle,
                    "episode-ranges",
                    reason,
                    sequence.Select(x => new MediaMappingReviewCandidate(
                        x.Provider,
                        x.ExternalId,
                        x.PreferredTitle,
                        0,
                        ["related PREQUEL/SEQUEL entry"],
                        x.Format,
                        x.SeasonYear,
                        x.EpisodeCount)).ToArray(),
                    cancellationToken);

                return AutomaticAnimeEpisodeMappingResult.Skipped(reason);
            }

            var regularPlan = AnimeSequenceMappingPlanner.Plan(
                regularEpisodes,
                sequence.Select(x => new RemoteAnimePart(
                    x.Provider,
                    x.ExternalId,
                    x.PreferredTitle,
                    x.EpisodeCount!.Value)).ToArray(),
                metadata.ExternalId);

            if (!regularPlan.CanApply)
            {
                await reviewStore.UpsertAsync(
                    "anime",
                    animeId.ToString(),
                    localTitle,
                    "episode-ranges",
                    regularPlan.Reason,
                    sequence.Select(x => new MediaMappingReviewCandidate(
                        x.Provider,
                        x.ExternalId,
                        x.PreferredTitle,
                        0,
                        ["related PREQUEL/SEQUEL entry"],
                        x.Format,
                        x.SeasonYear,
                        x.EpisodeCount)).ToArray(),
                    cancellationToken);

                return AutomaticAnimeEpisodeMappingResult.Skipped(
                    regularPlan.Reason);
            }

            plannedRanges.AddRange(regularPlan.Ranges);
        }

        if (specialEpisodes.Length > 0)
        {
            IReadOnlyList<AniListAnimeRelation> related;
            try
            {
                related = await aniListProvider.GetRelatedAnimeAsync(
                    metadata.ExternalId,
                    cancellationToken);
            }
            catch (MetadataProviderException exception)
            {
                await reviewStore.UpsertAsync(
                    "anime",
                    animeId.ToString(),
                    localTitle,
                    "episode-ranges",
                    exception.Message,
                    [],
                    cancellationToken);

                return AutomaticAnimeEpisodeMappingResult.Skipped(
                    exception.Message);
            }

            var specialPlan = AnimeSpecialMappingPlanner.Plan(
                specialEpisodes,
                related
                    .Where(x => x.Candidate.EpisodeCount is > 0)
                    .Select(x => new RemoteAnimeSpecialPart(
                        x.Candidate.Provider,
                        x.Candidate.ExternalId,
                        x.Candidate.PreferredTitle,
                        x.Candidate.EpisodeCount!.Value,
                        x.Candidate.Format ?? "",
                        x.RelationType))
                    .ToArray());

            reviewCandidates.AddRange(
                specialPlan.Candidates.Select(x =>
                    new MediaMappingReviewCandidate(
                        x.Provider,
                        x.ExternalId,
                        x.Title,
                        0,
                        [$"{x.RelationType} {x.Format} with {x.EpisodeCount} episode(s)"],
                        x.Format,
                        UnitCount: x.EpisodeCount)));

            if (!specialPlan.CanApply || specialPlan.Range is null)
            {
                await reviewStore.UpsertAsync(
                    "anime",
                    animeId.ToString(),
                    localTitle,
                    "episode-ranges",
                    specialPlan.Reason,
                    reviewCandidates,
                    cancellationToken);

                return AutomaticAnimeEpisodeMappingResult.Skipped(
                    specialPlan.Reason);
            }

            plannedRanges.Add(specialPlan.Range);
        }

        if (plannedRanges.Count == 0)
        {
            return AutomaticAnimeEpisodeMappingResult.Skipped(
                "No local episodes require automatic mapping.");
        }

        var added = new List<AnimeEpisodeMetadataMapping>();
        foreach (var range in plannedRanges)
        {
            var mapping = new AnimeEpisodeMetadataMapping(
                Guid.NewGuid(),
                animeId,
                range.SeasonNumber,
                range.LocalEpisodeStart,
                range.LocalEpisodeEnd,
                range.RemoteEpisodeStart,
                range.RemotePart.Provider,
                range.RemotePart.ExternalId,
                range.RemotePart.Title,
                range.RemotePart.EpisodeCount,
                DateTimeOffset.UtcNow);

            if (!await aniListStore.TryAddEpisodeMappingAsync(
                    mapping,
                    cancellationToken))
            {
                foreach (var previous in added)
                {
                    await aniListStore.RemoveEpisodeMappingAsync(
                        animeId,
                        previous.Id,
                        CancellationToken.None);
                }

                const string reason =
                    "An episode mapping changed concurrently; automatic mapping was rolled back.";
                await reviewStore.UpsertAsync(
                    "anime",
                    animeId.ToString(),
                    localTitle,
                    "episode-ranges",
                    reason,
                    reviewCandidates,
                    CancellationToken.None);

                return AutomaticAnimeEpisodeMappingResult.Skipped(reason);
            }

            added.Add(mapping);
        }

        await reviewStore.ResolveAsync(
            "anime",
            animeId.ToString(),
            "episode-ranges",
            cancellationToken);
        await SyncWorkIdentityAsync(animeId, cancellationToken);

        return new AutomaticAnimeEpisodeMappingResult(
            true,
            specialEpisodes.Length > 0
                ? "Regular episodes and Season 0 specials map uniquely to AniList."
                : "Local episodes map uniquely to the AniList sequel/part sequence.",
            added);
    }

    public async Task<AutomaticMediaMatchDecision> AutoMatchAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(animeId, cancellationToken);
        if (existing is not null)
        {
            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                ["Anime already has an explicit metadata match."]);
        }

        var local = await db.Anime
            .AsNoTracking()
            .Where(x => x.Id == animeId)
            .Select(x => new
            {
                x.Title,
                EpisodeCount = db.Episodes.Count(episode => episode.AnimeId == x.Id),
                SeasonCount = db.Episodes
                    .Where(episode => episode.AnimeId == x.Id)
                    .Select(episode => episode.SeasonNumber)
                    .Distinct()
                    .Count()
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (local is null)
        {
            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                ["Local anime was not found."]);
        }

        // Issue #568: automatic identity matching now searches whichever provider fills the
        // DisplayMetadata role for this anime instead of always AniList. With nothing configured
        // (BuiltIn source) that role resolves to AniList, reproducing today's behaviour exactly.
        var displayRole = await Roles().ResolveRoleForWorkAsync(
            animeId,
            MappingProviderRole.DisplayMetadata,
            cancellationToken);
        var provider = TryGetProvider(displayRole.Provider);
        if (provider is null)
        {
            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                [$"The display metadata provider configured for this anime ('{displayRole.Provider}') is not available."]);
        }

        IReadOnlyList<AnimeMetadataCandidate> candidates;
        try
        {
            candidates = await provider.SearchAsync(
                local.Title,
                8,
                cancellationToken);
        }
        catch (MetadataProviderException)
        {
            return new AutomaticMediaMatchDecision(
                AutomaticMediaMatchDisposition.None,
                null,
                0,
                0,
                [$"{provider.Key} metadata is currently unavailable."]);
        }

        var decision = AutomaticMediaMatcher.Select(
            new AutomaticMediaMatchInput(
                local.Title,
                UnitCount: local.SeasonCount == 1 && local.EpisodeCount > 0
                    ? local.EpisodeCount
                    : null,
                Format: "ANIME"),
            candidates.Select(candidate => new AutomaticMediaMatchCandidate(
                candidate.Provider,
                candidate.ExternalId,
                candidate.PreferredTitle,
                new[]
                {
                    candidate.PreferredTitle,
                    candidate.EnglishTitle ?? "",
                    candidate.RomajiTitle ?? "",
                    candidate.NativeTitle ?? ""
                },
                candidate.SeasonYear,
                candidate.EpisodeCount,
                candidate.Format)));

        if (decision.CanApply && decision.Candidate is not null)
        {
            var result = await MatchAsync(
                animeId,
                decision.Candidate.Provider,
                decision.Candidate.ExternalId,
                cancellationToken);

            if (!result.Success)
            {
                var reviewDecision = decision with
                {
                    Disposition = AutomaticMediaMatchDisposition.Review,
                    Evidence = decision.Evidence
                        .Append(result.Error ?? "Automatic match could not be persisted.")
                        .ToArray()
                };

                await SaveIdentityReviewAsync(
                    animeId,
                    local.Title,
                    reviewDecision,
                    cancellationToken);
                return reviewDecision;
            }

            await reviewStore.ResolveAsync(
                "anime",
                animeId.ToString(),
                "identity",
                cancellationToken);
        }
        else if (decision.Candidate is not null)
        {
            await SaveIdentityReviewAsync(
                animeId,
                local.Title,
                decision,
                cancellationToken);
        }

        return decision;
    }

    // Applies a local tvshow.nfo/episode NFO provider ID for a newly discovered anime that has no
    // metadata match yet. An AniList ID is used directly; a MyAnimeList ID is resolved to AniList
    // first (Jellyfin/Kodi NFOs commonly carry MAL rather than AniList IDs). Both paths only ever
    // touch an anime that has no existing match, and a lookup failure falls through to the
    // caller's normal automatic title matching instead of throwing.
    public async Task<AnimeMetadataMatchResult> MatchNfoProviderIdsAsync(
        Guid animeId,
        string? aniListId,
        string? myAnimeListId,
        CancellationToken cancellationToken)
    {
        if (await GetAsync(animeId, cancellationToken) is not null)
        {
            return new AnimeMetadataMatchResult(
                false,
                "Anime already has an explicit metadata match.");
        }

        if (aniListId is not null)
        {
            return await MatchAsync(
                animeId,
                AniListMetadataProvider.ProviderKey,
                aniListId,
                cancellationToken);
        }

        if (myAnimeListId is null)
        {
            return new AnimeMetadataMatchResult(
                false,
                "The local NFO carries no AniList or MyAnimeList ID.");
        }

        var aniListProvider = providers.OfType<AniListMetadataProvider>().FirstOrDefault();
        if (aniListProvider is null)
        {
            return new AnimeMetadataMatchResult(
                false,
                "The AniList metadata provider is not available.");
        }

        AnimeMetadataCandidate? candidate;
        try
        {
            candidate = await aniListProvider.GetByMalIdAsync(myAnimeListId, cancellationToken);
        }
        catch (MetadataProviderException exception)
        {
            return new AnimeMetadataMatchResult(false, exception.Message);
        }

        if (candidate is null)
        {
            return new AnimeMetadataMatchResult(
                false,
                "No AniList entry was found for the local MyAnimeList ID.");
        }

        return await MatchAsync(
            animeId,
            candidate.Provider,
            candidate.ExternalId,
            cancellationToken);
    }

    // A season.nfo AniList ID (as written by the Jellyfin AniList plugin) feeds the existing
    // episode-range mapping flow for that one local season, but only when the season has no
    // manual or automatic mapping yet: a mapping already present is left untouched rather than
    // silently replaced. A season that cannot be mapped automatically (an unknown AniList entry,
    // a concurrent mapping change) becomes a mapping review task instead of being dropped.
    public async Task<AnimeMetadataMatchResult> MatchSeasonAniListIdAsync(
        Guid animeId,
        int seasonNumber,
        string aniListId,
        CancellationToken cancellationToken)
    {
        var existingMappings = await aniListStore.LoadEpisodeMappingsAsync(
            animeId,
            cancellationToken);
        if (existingMappings.Any(x => x.SeasonNumber == seasonNumber))
        {
            return new AnimeMetadataMatchResult(
                false,
                $"Season {seasonNumber} already has an episode mapping.");
        }

        // Same EpisodeStructure gate as AutoMapEpisodeRangesAsync: an anime explicitly opted out
        // of AniList-numbered structure keeps its season.nfo AniList ID from being applied.
        var structureRole = await Roles().ResolveRoleForWorkAsync(
            animeId,
            MappingProviderRole.EpisodeStructure,
            cancellationToken);
        if (structureRole.Source != ProviderRoleSource.BuiltIn &&
            !string.Equals(structureRole.Provider, MappingProviders.AniList, StringComparison.OrdinalIgnoreCase))
        {
            return new AnimeMetadataMatchResult(
                false,
                $"Episode structure for this anime is configured to use '{structureRole.Provider}', not AniList; the season.nfo AniList ID was not applied.");
        }

        var localSeasonNumbers = await db.Episodes
            .AsNoTracking()
            .Where(x => x.AnimeId == animeId && x.SeasonNumber == seasonNumber)
            .OrderBy(x => x.Number)
            .Select(x => x.Number)
            .ToListAsync(cancellationToken);

        if (localSeasonNumbers.Count == 0)
        {
            return new AnimeMetadataMatchResult(
                false,
                $"Local season {seasonNumber} was not found.");
        }

        AnimeMetadataMatchResult result;
        try
        {
            result = await MatchEpisodeRangeAsync(
                animeId,
                seasonNumber,
                localSeasonNumbers[0],
                null,
                1,
                AniListMetadataProvider.ProviderKey,
                aniListId,
                cancellationToken);
        }
        catch (MetadataProviderException exception)
        {
            result = new AnimeMetadataMatchResult(false, exception.Message);
        }

        if (!result.Success)
        {
            var localTitle = await db.Anime
                .AsNoTracking()
                .Where(x => x.Id == animeId)
                .Select(x => x.Title)
                .SingleOrDefaultAsync(cancellationToken)
                ?? animeId.ToString();

            await reviewStore.UpsertAsync(
                "anime",
                animeId.ToString(),
                localTitle,
                $"episode-ranges:season-{seasonNumber}",
                $"The season.nfo AniList ID for season {seasonNumber} could not be applied automatically: {result.Error}",
                [],
                cancellationToken);
        }

        return result;
    }

    public async Task<AnimeMetadataMatchResult> MatchEpisodeRangeAsync(
        Guid animeId,
        int seasonNumber,
        int localEpisodeStart,
        int? localEpisodeEnd,
        int remoteEpisodeStart,
        string providerKey,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (seasonNumber < 0)
        {
            return new AnimeMetadataMatchResult(false, "Local season must be zero or greater.");
        }

        if (localEpisodeStart <= 0)
        {
            return new AnimeMetadataMatchResult(false, "Local episode start must be greater than zero.");
        }

        if (remoteEpisodeStart <= 0)
        {
            return new AnimeMetadataMatchResult(false, "AniList episode start must be greater than zero.");
        }

        var localNumbers = await db.Episodes
            .AsNoTracking()
            .Where(x => x.AnimeId == animeId && x.SeasonNumber == seasonNumber)
            .OrderBy(x => x.Number)
            .Select(x => x.Number)
            .ToListAsync(cancellationToken);

        if (localNumbers.Count == 0)
        {
            return new AnimeMetadataMatchResult(false, $"Local season {seasonNumber} was not found.");
        }

        if (!localNumbers.Contains(localEpisodeStart))
        {
            return new AnimeMetadataMatchResult(
                false,
                $"Local episode S{seasonNumber:00}E{localEpisodeStart:00} was not found.");
        }

        var provider = GetProvider(providerKey);
        var candidate = await provider.GetAsync(externalId, cancellationToken);
        if (candidate is null)
        {
            return new AnimeMetadataMatchResult(false, "The AniList entry was not found.");
        }

        var localSeasonMaximum = localNumbers.Max();
        var resolvedEnd = localEpisodeEnd ?? AnimeEpisodeMetadataRules.ResolveAutomaticLocalEnd(
            localEpisodeStart,
            localSeasonMaximum,
            remoteEpisodeStart,
            candidate.EpisodeCount);

        if (resolvedEnd < localEpisodeStart || resolvedEnd > localSeasonMaximum)
        {
            return new AnimeMetadataMatchResult(
                false,
                $"Local episode end must be between {localEpisodeStart} and {localSeasonMaximum}.");
        }

        var remoteEpisodeEnd =
            remoteEpisodeStart + (resolvedEnd - localEpisodeStart);

        if (candidate.EpisodeCount is > 0 &&
            remoteEpisodeEnd > candidate.EpisodeCount.Value)
        {
            return new AnimeMetadataMatchResult(
                false,
                $"The mapping would reach AniList episode {remoteEpisodeEnd}, above the known episode count ({candidate.EpisodeCount}).");
        }

        var mapping = new AnimeEpisodeMetadataMapping(
            Guid.NewGuid(),
            animeId,
            seasonNumber,
            localEpisodeStart,
            resolvedEnd,
            remoteEpisodeStart,
            candidate.Provider,
            candidate.ExternalId,
            candidate.PreferredTitle,
            candidate.EpisodeCount,
            DateTimeOffset.UtcNow);

        try
        {
            var added = await aniListStore.TryAddEpisodeMappingAsync(
                mapping,
                cancellationToken);

            if (added)
            {
                await SyncWorkIdentityAsync(animeId, cancellationToken);
            }

            return added
                ? new AnimeMetadataMatchResult(true)
                : new AnimeMetadataMatchResult(
                    false,
                    "This local episode range overlaps an existing AniList mapping.");
        }
        catch (AniListAccountException exception)
        {
            return new AnimeMetadataMatchResult(false, exception.Message);
        }
    }

    public async Task<bool> RemoveEpisodeMappingAsync(
        Guid animeId,
        Guid mappingId,
        CancellationToken cancellationToken)
    {
        var removed = await aniListStore.RemoveEpisodeMappingAsync(animeId, mappingId, cancellationToken);
        if (removed)
        {
            await SyncWorkIdentityAsync(animeId, cancellationToken);
        }

        return removed;
    }

    public async Task<ResolvedAnimeEpisodeMetadata?> ResolveEpisodeAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var episode = await db.Episodes
            .AsNoTracking()
            .Where(x => x.Id == episodeId)
            .Select(x => new
            {
                x.AnimeId,
                x.SeasonNumber,
                x.Number
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (episode is null || episode.Number <= 0)
        {
            return null;
        }

        var mappings = await aniListStore.LoadEpisodeMappingsAsync(
            episode.AnimeId,
            cancellationToken);

        var explicitMapping = mappings.SingleOrDefault(
            x => x.Contains(episode.SeasonNumber, episode.Number));

        if (explicitMapping is not null)
        {
            return new ResolvedAnimeEpisodeMetadata(
                explicitMapping.Provider,
                explicitMapping.ExternalId,
                explicitMapping.PreferredTitle,
                explicitMapping.ResolveRemoteEpisode(episode.Number),
                explicitMapping.EpisodeCount,
                IsExplicitRange: true);
        }

        // Once explicit ranges exist, never guess for uncovered episodes.
        // This prevents a split cour/part from falling back to the display match.
        if (mappings.Count > 0)
        {
            return null;
        }

        var seasonCount = await db.Episodes
            .AsNoTracking()
            .Where(x => x.AnimeId == episode.AnimeId)
            .Select(x => x.SeasonNumber)
            .Distinct()
            .Take(2)
            .CountAsync(cancellationToken);

        if (seasonCount != 1)
        {
            return null;
        }

        var metadata = await GetAsync(episode.AnimeId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        return new ResolvedAnimeEpisodeMetadata(
            metadata.Provider,
            metadata.ExternalId,
            metadata.PreferredTitle,
            episode.Number,
            metadata.EpisodeCount,
            IsExplicitRange: false);
    }

    public async Task<IReadOnlyList<AnimeMetadataCandidate>> SearchAsync(
        string providerKey,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var provider = GetProvider(providerKey);
        return await provider.SearchAsync(query, limit, cancellationToken);
    }

    public async Task<AnimeMetadataMatchResult> MatchAsync(
        Guid animeId,
        string providerKey,
        string externalId,
        CancellationToken cancellationToken)
    {
        var animeExists = await db.Anime
            .AsNoTracking()
            .AnyAsync(x => x.Id == animeId, cancellationToken);

        if (!animeExists)
        {
            return new AnimeMetadataMatchResult(false, "Local anime was not found.");
        }

        var candidate = await GetCandidateAsync(providerKey, externalId, cancellationToken);
        if (candidate is null)
        {
            return new AnimeMetadataMatchResult(false, "The metadata entry was not found.");
        }

        return await MatchAsync(animeId, candidate, cancellationToken);
    }

    public Task<AnimeMetadataCandidate?> GetCandidateAsync(
        string providerKey,
        string externalId,
        CancellationToken cancellationToken) =>
        GetProvider(providerKey).GetAsync(externalId, cancellationToken);

    /// <summary>Stores an entry the caller already loaded from its provider as the anime's match.</summary>
    public async Task<AnimeMetadataMatchResult> MatchAsync(
        Guid animeId,
        AnimeMetadataCandidate candidate,
        CancellationToken cancellationToken)
    {
        var usedByOtherAnime = await db.AnimeMetadata
            .AsNoTracking()
            .AnyAsync(
                x => x.Provider == candidate.Provider &&
                    x.ExternalId == candidate.ExternalId &&
                    x.AnimeId != animeId,
                cancellationToken);

        if (usedByOtherAnime)
        {
            return new AnimeMetadataMatchResult(
                false,
                "This metadata entry is already matched to another local anime.");
        }

        var metadata = await db.AnimeMetadata
            .SingleOrDefaultAsync(x => x.AnimeId == animeId, cancellationToken);

        if (metadata is null)
        {
            metadata = new AnimeMetadata { AnimeId = animeId };
            db.AnimeMetadata.Add(metadata);
        }

        Apply(metadata, candidate);
        await db.SaveChangesAsync(cancellationToken);

        await reviewStore.ResolveAsync(
            "anime",
            animeId.ToString(),
            "identity",
            cancellationToken);
        await SyncWorkIdentityAsync(animeId, cancellationToken);

        return new AnimeMetadataMatchResult(true);
    }

    public async Task<bool> RefreshAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var metadata = await db.AnimeMetadata
            .SingleOrDefaultAsync(x => x.AnimeId == animeId, cancellationToken);

        if (metadata is null)
        {
            return false;
        }

        var provider = GetProvider(metadata.Provider);
        var candidate = await provider.GetAsync(metadata.ExternalId, cancellationToken);
        if (candidate is null)
        {
            return false;
        }

        Apply(metadata, candidate);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<int> RemoveAsync(
        Guid animeId,
        CancellationToken cancellationToken) =>
        db.AnimeMetadata
            .Where(x => x.AnimeId == animeId)
            .ExecuteDeleteAsync(cancellationToken);

    private async Task SaveIdentityReviewAsync(
        Guid animeId,
        string localTitle,
        AutomaticMediaMatchDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.Candidate is null)
        {
            return;
        }

        var reason = decision.Disposition == AutomaticMediaMatchDisposition.Review
            ? $"AniList identity needs review: score {decision.Score}, runner-up {decision.RunnerUpScore}."
            : $"AniList identity confidence is too low for automatic matching: score {decision.Score}.";

        await reviewStore.UpsertAsync(
            "anime",
            animeId.ToString(),
            localTitle,
            "identity",
            reason,
            [
                new MediaMappingReviewCandidate(
                    decision.Candidate.Provider,
                    decision.Candidate.ExternalId,
                    decision.Candidate.PreferredTitle,
                    decision.Score,
                    decision.Evidence,
                    decision.Candidate.Format,
                    decision.Candidate.Year,
                    decision.Candidate.UnitCount)
            ],
            cancellationToken);
    }

    private IAnimeMetadataProvider GetProvider(string providerKey) =>
        TryGetProvider(providerKey)
        ?? throw new InvalidOperationException(
            $"Metadata provider '{providerKey}' is not registered.");

    // Non-throwing lookup for role-resolved providers: a role can legitimately resolve to a
    // provider this Jularr instance has no IAnimeMetadataProvider for yet (e.g. Tvdb/Tmdb/Mal),
    // which must degrade to "not available" rather than throw.
    private IAnimeMetadataProvider? TryGetProvider(string providerKey) =>
        providers.FirstOrDefault(
            provider => provider.Key.Equals(providerKey, StringComparison.OrdinalIgnoreCase));

    private static void Apply(
        AnimeMetadata metadata,
        AnimeMetadataCandidate candidate)
    {
        metadata.Provider = candidate.Provider;
        metadata.ExternalId = candidate.ExternalId;
        metadata.PreferredTitle = candidate.PreferredTitle;
        metadata.RomajiTitle = candidate.RomajiTitle;
        metadata.EnglishTitle = candidate.EnglishTitle;
        metadata.NativeTitle = candidate.NativeTitle;
        metadata.Description = candidate.Description;
        metadata.CoverImageUrl = candidate.CoverImageUrl;
        metadata.BannerImageUrl = candidate.BannerImageUrl;
        metadata.Format = candidate.Format;
        metadata.Status = candidate.Status;
        metadata.Season = candidate.Season;
        metadata.SeasonYear = candidate.SeasonYear;
        metadata.EpisodeCount = candidate.EpisodeCount;
        metadata.EpisodeDurationMinutes = candidate.EpisodeDurationMinutes;
        metadata.AverageScore = candidate.AverageScore;
        metadata.UpdatedAt = DateTime.UtcNow;
    }
}
