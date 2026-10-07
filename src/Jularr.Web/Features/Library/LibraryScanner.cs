using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Subtitles;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

public sealed class LibraryScanner(
    AppDbContext db,
    SubtitleImportService subtitleImport,
    EmbeddedSubtitleExtractor embeddedSubtitleExtractor,
    MediaInventoryService mediaInventory,
    SonarrArtworkSyncService sonarrArtworkSync,
    ILogger<LibraryScanner> logger,
    AnimeMetadataService? metadataService = null,
    MediaSegmentSidecarImporter? segmentSidecars = null,
    AnimeArtworkLibrary? artworkLibrary = null,
    CanonicalVideoStorageBackfillService? canonicalVideoBackfill = null)
{
    private readonly AnimeArtworkLibrary artwork =
        artworkLibrary ?? new AnimeArtworkLibrary(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AnimeArtworkLibrary>.Instance);

    internal static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".webm"
    };

    public Task<ScanResult> ScanAsync(Guid rootId, CancellationToken cancellationToken) =>
        ReconcileAsync(rootId, null, null, cancellationToken);

    public Task<ScanResult> ScanAsync(
        Guid rootId,
        LibraryScanProgressHandler? progress,
        CancellationToken cancellationToken) =>
        ReconcileAsync(rootId, null, progress, cancellationToken);

    // Reconciles one folder below the root (normally an anime directory) with exactly the
    // rules of a full scan, but only touches media files inside that folder. A folder that
    // no longer exists reconciles as a deletion of its media.
    public Task<ScanResult> ScanFolderAsync(
        Guid rootId,
        string relativeFolder,
        LibraryScanProgressHandler? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeFolder);
        return ReconcileAsync(rootId, relativeFolder, progress, cancellationToken);
    }

    private async Task<ScanResult> ReconcileAsync(
        Guid rootId,
        string? relativeFolder,
        LibraryScanProgressHandler? progress,
        CancellationToken cancellationToken)
    {
        var root = await db.LibraryRoots.SingleAsync(x => x.Id == rootId, cancellationToken);
        if (!root.IsEnabled || !await db.LibraryRoots.ServingAnime(db).AnyAsync(x => x.Id == rootId, cancellationToken))
        {
            return new ScanResult(0, 0, 0, 0);
        }

        var rootPath = Path.GetFullPath(root.Path);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Library root does not exist: {rootPath}");
        }

        var scopePath = ResolveScopePath(rootPath, relativeFolder);
        var scopePrefix = scopePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        await ReportAsync(progress, LibraryScanPhase.Enumerating, 0, 0, cancellationToken);

        var candidates = new List<FileInfo>();
        var nfoFiles = new NfoFileIndex();
        try
        {
            foreach (var path in Directory.Exists(scopePath)
                         ? Directory.EnumerateFiles(scopePath, "*", SearchOption.AllDirectories)
                         : [])
            {
                if (MediaExtensions.Contains(Path.GetExtension(path)))
                {
                    candidates.Add(new FileInfo(path));
                }
                else if (NfoFileIndex.IsNfo(path))
                {
                    nfoFiles.Add(path);
                }
            }

            // Ordinal order makes the "first file wins" rules (series folder, NFO title) deterministic.
            candidates.Sort((left, right) => string.CompareOrdinal(left.FullName, right.FullName));
        }
        catch (IOException exception)
        {
            throw new IOException(
                $"Library root could not be enumerated safely: {rootPath}",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException(
                $"Library root could not be enumerated safely: {rootPath}",
                exception);
        }

        var observedMediaPaths = candidates
            .Select(file => Path.GetFullPath(file.FullName))
            .ToHashSet(StringComparer.Ordinal);

        var existingFiles = await db.MediaFiles
            .Where(x => x.LibraryRootId == rootId)
            .ToDictionaryAsync(x => x.Path, StringComparer.Ordinal, cancellationToken);

        if (relativeFolder is not null)
        {
            existingFiles = existingFiles
                .Where(pair => pair.Key.StartsWith(scopePrefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            // A vanished folder only counts as deleted while the root itself still has content;
            // an empty mount point is an unavailable NAS, exactly like the full-scan guard below.
            if (existingFiles.Count > 0 &&
                candidates.Count == 0 &&
                !Directory.EnumerateFileSystemEntries(rootPath).Any())
            {
                throw new IOException(
                    "Library root is empty while Jularr still has known media in the scanned folder. " +
                    "Reconciliation was stopped to avoid treating an unavailable NAS mount as a mass deletion.");
            }
        }
        else if (existingFiles.Count > 0 && candidates.Count == 0)
        {
            throw new IOException(
                "Library root returned no media files while Jularr still has known media for it. " +
                "Reconciliation was stopped to avoid treating an unavailable NAS mount as a mass deletion.");
        }

        var warnings = new List<ScanWarning>();
        var warningCount = 0;
        var errors = 0;
        var processed = 0;

        var relinkPlan = await PlanRelinksAsync(
            existingFiles.Values
                .Where(mediaFile => !observedMediaPaths.Contains(mediaFile.Path))
                .ToArray(),
            candidates
                .Where(file => !existingFiles.ContainsKey(Path.GetFullPath(file.FullName)))
                .ToArray(),
            cancellationToken);
        foreach (var path in relinkPlan.Ambiguous)
        {
            AddWarning(warnings, ref warningCount, rootPath, path, "Needs attention: possibly moved media");
        }

        var relinked = 0;
        var relinkedFromEpisodeIds = new HashSet<Guid>();

        var animeByKey = await db.Anime.ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, cancellationToken);
        var episodes = await db.Episodes.ToListAsync(cancellationToken);
        var episodeByKey = episodes.ToDictionary(
            x => (x.AnimeId, x.SeasonNumber, x.Number),
            x => x);

        var discovered = 0;
        var updated = 0;
        var skipped = 0;
        var subtitleCandidates = new List<SubtitleCandidate>();
        var artworkDirectories = new Dictionary<Guid, string>();
        var artworkSeasons = new Dictionary<Guid, Dictionary<int, string?>>();
        var newlyDiscoveredAnimeIds = new HashSet<Guid>();
        var nfoAniListIds = new Dictionary<Guid, string>();
        var nfoMalIds = new Dictionary<Guid, string>();
        var nfoSeasonAniListIds = new Dictionary<(Guid AnimeId, int SeasonNumber), string>();
        var processedSeasons = new HashSet<(Guid AnimeId, int SeasonNumber)>();
        var nfoTitledEpisodes = new HashSet<(Guid AnimeId, int SeasonNumber, int EpisodeNumber)>();
        var localMetadataByAnimeId = await db.AnimeLocalMetadata
            .ToDictionaryAsync(x => x.AnimeId, cancellationToken);
        var metadataWarnings = 0;

        await ReportAsync(progress, LibraryScanPhase.Reconciling, 0, candidates.Count, cancellationToken);

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReportAsync(progress, LibraryScanPhase.Reconciling, processed++, candidates.Count, cancellationToken);

            var normalizedPath = Path.GetFullPath(file.FullName);
            if (!MediaPathParser.TryParse(rootPath, normalizedPath, out var descriptor))
            {
                skipped++;
                AddWarning(warnings, ref warningCount, rootPath, normalizedPath, "Unmatched media file");
                continue;
            }

            if (!animeByKey.TryGetValue(descriptor.AnimeKey, out var anime))
            {
                anime = new Anime { Key = descriptor.AnimeKey, Title = descriptor.AnimeTitle };
                animeByKey.Add(anime.Key, anime);
                db.Anime.Add(anime);
                newlyDiscoveredAnimeIds.Add(anime.Id);
            }

            var animeDirectory = TryGetAnimeDirectory(rootPath, normalizedPath);
            if (animeDirectory is not null)
            {
                // A season's artwork belongs in its own folder; null means the season's episodes
                // sit directly in the series folder.
                var mediaDirectory = Path.GetDirectoryName(normalizedPath);
                var seasons = artworkSeasons.TryGetValue(anime.Id, out var known)
                    ? known
                    : artworkSeasons[anime.Id] = [];
                seasons.TryAdd(
                    descriptor.SeasonNumber,
                    string.Equals(mediaDirectory, animeDirectory, StringComparison.Ordinal) ? null : mediaDirectory);
            }

            if (animeDirectory is not null &&
                artworkDirectories.TryAdd(anime.Id, animeDirectory) &&
                nfoFiles.FindShow(animeDirectory) is { } showNfoPath)
            {
                var showNfoFile = new FileInfo(showNfoPath);
                var existingLocalMetadata = localMetadataByAnimeId.GetValueOrDefault(anime.Id);

                // Skip re-parsing a tvshow.nfo whose size and last-write time have not changed
                // since it was last read; nothing it could tell us has changed either.
                var showNfoUnchanged = existingLocalMetadata is not null &&
                    existingLocalMetadata.SourceFileSizeBytes == showNfoFile.Length &&
                    existingLocalMetadata.SourceFileLastWriteTimeUtc == showNfoFile.LastWriteTimeUtc;

                if (!showNfoUnchanged)
                {
                    var show = NfoReader.ReadShow(showNfoPath);
                    if (show.Value is null)
                    {
                        metadataWarnings++;
                        LogRejectedNfo(showNfoPath, show.Warning);
                        AddWarning(warnings, ref warningCount, rootPath, showNfoPath, "Ignored NFO");
                    }
                    else
                    {
                        if (show.Value.Title is { } showTitle &&
                            !string.Equals(anime.Title, showTitle, StringComparison.Ordinal))
                        {
                            anime.Title = showTitle;
                            if (!newlyDiscoveredAnimeIds.Contains(anime.Id))
                            {
                                updated++;
                            }
                        }

                        if (show.Value.ProviderIds.AniList is { } aniListId)
                        {
                            nfoAniListIds[anime.Id] = aniListId;
                        }
                        else if (show.Value.ProviderIds.MyAnimeList is { } malId)
                        {
                            nfoMalIds[anime.Id] = malId;
                        }

                        ApplyLocalMetadata(
                            db,
                            localMetadataByAnimeId,
                            anime.Id,
                            show.Value,
                            showNfoFile);
                    }
                }
            }

            // Kodi/Jellyfin season.nfo sits beside the episode files of that season; read it once
            // per anime/season pair per scan and carry only its AniList ID forward.
            var seasonKey = (anime.Id, descriptor.SeasonNumber);
            if (processedSeasons.Add(seasonKey) &&
                Path.GetDirectoryName(normalizedPath) is { } seasonDirectory &&
                nfoFiles.FindSeason(seasonDirectory) is { } seasonNfoPath)
            {
                var season = NfoReader.ReadSeason(seasonNfoPath);
                if (season.Value is null)
                {
                    metadataWarnings++;
                    LogRejectedNfo(seasonNfoPath, season.Warning);
                    AddWarning(warnings, ref warningCount, rootPath, seasonNfoPath, "Ignored NFO");
                }
                else if (season.Value.ProviderIds.AniList is { } seasonAniListId)
                {
                    nfoSeasonAniListIds[seasonKey] = seasonAniListId;
                }
            }

            var episodeKey = (anime.Id, descriptor.SeasonNumber, descriptor.EpisodeNumber);
            var episodeNfoPath = nfoFiles.FindEpisode(normalizedPath);
            var episodeTitle = ResolveEpisodeTitle(
                episodeNfoPath,
                descriptor,
                nfoTitledEpisodes.Contains(episodeKey),
                ref metadataWarnings,
                out var titleFromNfo,
                out var nfoRejected);
            if (nfoRejected)
            {
                AddWarning(warnings, ref warningCount, rootPath, episodeNfoPath!, "Ignored NFO");
            }
            if (titleFromNfo)
            {
                nfoTitledEpisodes.Add(episodeKey);
            }

            if (!episodeByKey.TryGetValue(episodeKey, out var episode))
            {
                episode = new Episode
                {
                    AnimeId = anime.Id,
                    SeasonNumber = descriptor.SeasonNumber,
                    Number = descriptor.EpisodeNumber,
                    Title = episodeTitle ?? descriptor.EpisodeTitle
                };
                episodeByKey.Add(episodeKey, episode);
                db.Episodes.Add(episode);
            }
            else if (episodeTitle is not null &&
                     !string.Equals(
                         episode.Title,
                         episodeTitle,
                         StringComparison.Ordinal))
            {
                episode.Title = episodeTitle;
                updated++;
            }

            if (file.Length == 0)
            {
                AddWarning(warnings, ref warningCount, rootPath, normalizedPath, "Empty media file");
            }

            var lastWrite = file.LastWriteTimeUtc;
            if (existingFiles.TryGetValue(normalizedPath, out var mediaFile))
            {
                if (mediaFile.SizeBytes != file.Length || mediaFile.LastWriteTimeUtc != lastWrite)
                {
                    mediaFile.SizeBytes = file.Length;
                    mediaFile.LastWriteTimeUtc = lastWrite;
                    updated++;
                }
            }
            else if (relinkPlan.Moves.TryGetValue(normalizedPath, out var moved))
            {
                // The same file moved or renamed outside Jularr: keep its identity (analysis,
                // segments, history) instead of removing it and discovering a new one.
                existingFiles.Remove(moved.Path);
                moved.Path = normalizedPath;
                if (moved.EpisodeId != episode.Id)
                {
                    if (moved.EpisodeId is { } previousEpisodeId)
                    {
                        relinkedFromEpisodeIds.Add(previousEpisodeId);
                    }

                    moved.EpisodeId = episode.Id;
                    // The same physical file moved to a different logical episode: preserve the
                    // StoredFile id/analysis but force the canonical Asset target to be reconciled.
                    moved.MediaAssetId = null;
                }

                existingFiles.Add(normalizedPath, moved);
                relinked++;
            }
            else
            {
                mediaFile = new MediaFile
                {
                    LibraryRootId = rootId,
                    EpisodeId = episode.Id,
                    Path = normalizedPath,
                    SizeBytes = file.Length,
                    LastWriteTimeUtc = lastWrite
                };
                db.MediaFiles.Add(mediaFile);
                existingFiles.Add(normalizedPath, mediaFile);
                discovered++;
            }

            subtitleCandidates.Add(new SubtitleCandidate(
                episode.Id,
                normalizedPath,
                lastWrite));
        }

        var staleMediaFiles = existingFiles.Values
            .Where(mediaFile => !observedMediaPaths.Contains(mediaFile.Path))
            .ToArray();

        if (staleMediaFiles.Length > 0)
        {
            // Missing files are removed only while the root is still demonstrably readable: a
            // mount that dropped during the scan must never look like deleted media.
            EnsureRootStillReadable(rootPath);
            staleMediaFiles = staleMediaFiles
                .Where(mediaFile => !File.Exists(mediaFile.Path))
                .ToArray();
            db.MediaFiles.RemoveRange(staleMediaFiles);
        }

        if (relativeFolder is null)
        {
            root.LastScannedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        // Anime files still enter through the scanner as legacy rows; each scan gives the new ones their canonical Work, episode and Asset, so an
        // imported or dropped episode is playable through the canonical Library without any startup or background backfill.
        if (canonicalVideoBackfill is not null)
        {
            await canonicalVideoBackfill.BackfillLegacyAnimeAsync(rootId, cancellationToken);
        }

        if (metadataService is not null)
        {
            var matched = 0;
            foreach (var animeId in newlyDiscoveredAnimeIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReportAsync(progress, LibraryScanPhase.Metadata, matched++, newlyDiscoveredAnimeIds.Count, cancellationToken);
                try
                {
                    var hasAniListId = nfoAniListIds.TryGetValue(animeId, out var aniListId);
                    var hasMalId = nfoMalIds.TryGetValue(animeId, out var malId);
                    if (hasAniListId || hasMalId)
                    {
                        await MatchNfoProviderIdAsync(
                            metadataService,
                            animeId,
                            hasAniListId ? aniListId : null,
                            hasMalId ? malId : null,
                            cancellationToken);
                    }

                    var decision = await metadataService.AutoMatchAsync(
                        animeId,
                        cancellationToken);
                    if (decision.CanApply && decision.Candidate is not null)
                    {
                        logger.LogInformation(
                            "Automatically matched anime {AnimeId} to {Provider}:{ExternalId} with score {Score}.",
                            animeId,
                            decision.Candidate.Provider,
                            decision.Candidate.ExternalId,
                            decision.Score);
                    }

                    var episodeMapping = await metadataService.AutoMapEpisodeRangesAsync(
                        animeId,
                        cancellationToken);
                    if (episodeMapping.Applied)
                    {
                        logger.LogInformation(
                            "Automatically mapped {RangeCount} AniList episode range(s) for anime {AnimeId}.",
                            episodeMapping.Mappings.Count,
                            animeId);
                    }
                }
                catch (Exception exception) when (
                    exception is MetadataProviderException or InvalidOperationException)
                {
                    errors++;
                    logger.LogWarning(
                        exception,
                        "Automatic metadata matching failed for anime {AnimeId}; the local library scan remains valid.",
                        animeId);
                }
            }

            // A season.nfo AniList ID applies to any anime it belongs to, not only newly
            // discovered ones (a later season can gain its own season.nfo at any time).
            foreach (var ((seasonAnimeId, seasonNumber), seasonAniListId) in nfoSeasonAniListIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var seasonMatch = await metadataService.MatchSeasonAniListIdAsync(
                        seasonAnimeId,
                        seasonNumber,
                        seasonAniListId,
                        cancellationToken);
                    if (seasonMatch.Success)
                    {
                        logger.LogInformation(
                            "Applied the season.nfo AniList ID {ExternalId} to anime {AnimeId} season {SeasonNumber}.",
                            seasonAniListId,
                            seasonAnimeId,
                            seasonNumber);
                    }
                }
                catch (Exception exception) when (
                    exception is MetadataProviderException or InvalidOperationException)
                {
                    errors++;
                    logger.LogWarning(
                        exception,
                        "Automatic season.nfo episode-range mapping failed for anime {AnimeId} season {SeasonNumber}; the local library scan remains valid.",
                        seasonAnimeId,
                        seasonNumber);
                }
            }
        }

        var removed = staleMediaFiles.Length;
        if (removed > 0 || relinkedFromEpisodeIds.Count > 0)
        {
            var staleEpisodeIds = staleMediaFiles
                .Where(x => x.EpisodeId.HasValue)
                .Select(x => x.EpisodeId!.Value)
                .Concat(relinkedFromEpisodeIds)
                .Distinct()
                .ToArray();

            var orphanEpisodeIds = await db.Episodes
                .Where(episode =>
                    staleEpisodeIds.Contains(episode.Id) &&
                    !db.MediaFiles.Any(media => media.EpisodeId == episode.Id))
                .Select(episode => episode.Id)
                .ToArrayAsync(cancellationToken);

            if (orphanEpisodeIds.Length > 0)
            {
                var orphanEpisodes = await db.Episodes
                    .Where(x => orphanEpisodeIds.Contains(x.Id))
                    .ToArrayAsync(cancellationToken);
                db.Episodes.RemoveRange(orphanEpisodes);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        await ReportAsync(progress, LibraryScanPhase.Artwork, 0, artworkDirectories.Count, cancellationToken);

        // The Sonarr artwork sync is library-wide; a folder scan only needs it for anime it
        // just discovered, everything else was synced by an earlier full reconciliation.
        if (relativeFolder is null || newlyDiscoveredAnimeIds.Count > 0)
        {
            await sonarrArtworkSync.SyncIfConfiguredAsync(cancellationToken);
        }

        if (segmentSidecars is not null)
        {
            await segmentSidecars.ReconcileRootAsync(rootId, cancellationToken);
        }

        var localArtworkImported = 0;
        var localArtworkUnchanged = 0;
        var artworkProcessed = 0;
        var artworkAnimeIds = artworkDirectories.Keys.ToArray();
        var providerArtwork = new Dictionary<Guid, AnimeProviderArtwork?>();
        foreach (var metadata in await db.AnimeMetadata
                     .AsNoTracking()
                     .Where(x => artworkAnimeIds.Contains(x.AnimeId))
                     .Select(x => new { x.AnimeId, x.Provider, x.CoverImageUrl, x.BannerImageUrl })
                     .ToListAsync(cancellationToken))
        {
            // Issue #568: the provider artwork picked up here is only used when it matches the
            // anime's resolved Artwork role; see AnimeArtworkSourceResolver.
            providerArtwork.TryAdd(
                metadata.AnimeId,
                await AnimeArtworkSourceResolver.ResolveForAnimeAsync(
                    db,
                    metadata.AnimeId,
                    metadata.Provider,
                    metadata.CoverImageUrl,
                    metadata.BannerImageUrl,
                    cancellationToken));
        }

        foreach (var (animeId, animeDirectory) in artworkDirectories)
        {
            await ReportAsync(progress, LibraryScanPhase.Artwork, artworkProcessed++, artworkDirectories.Count, cancellationToken);
            var reconciled = await artwork.ReconcileAsync(
                animeId,
                animeDirectory,
                artworkSeasons.GetValueOrDefault(animeId) ?? [],
                providerArtwork.GetValueOrDefault(animeId),
                cancellationToken);
            localArtworkImported += reconciled.Refreshed + reconciled.Migrated;
            localArtworkUnchanged += reconciled.Unchanged;
        }

        if (relativeFolder is null && artwork.CountLegacyFolders() is > 0 and var legacyFolders)
        {
            logger.LogInformation(
                "{Count} anime artwork folder(s) under {LegacyRoot} are not migrated beside the media yet; their anime was not found in a scanned library folder.",
                legacyFolders,
                artwork.Cache.LegacyRootPath);
        }

        // Runs ffprobe only for new/changed files or after a probe version bump; invalid media is
        // recorded as a diagnostic on its analysis instead of failing the root. Embedded subtitle
        // selection below reads this inventory.
        await ReportAsync(progress, LibraryScanPhase.Analyzing, 0, 0, cancellationToken);
        var inventory = await mediaInventory.ReconcileAsync(
            rootId,
            relativeFolder is null ? null : scopePrefix,
            cancellationToken);

        var episodeIds = subtitleCandidates
            .Select(x => x.EpisodeId)
            .Distinct()
            .ToArray();

        var embeddedTracks = episodeIds.Length == 0
            ? new List<ExistingEmbeddedTrack>()
            : await db.SubtitleTracks
                .AsNoTracking()
                .Where(x =>
                    episodeIds.Contains(x.EpisodeId) &&
                    x.Path.StartsWith(EmbeddedSubtitleExtractor.SourcePrefix))
                .Select(x => new ExistingEmbeddedTrack(
                    x.EpisodeId,
                    x.Path,
                    x.SourceUpdatedAt))
                .ToListAsync(cancellationToken);

        var subtitleFiles = 0;
        var subtitleEpisodes = 0;
        var sidecarListings = new SubtitleSidecarDirectoryCache();
        var contentLanguage = await new LearningContentLanguageResolver(db)
            .ResolveTargetLanguageAsync(cancellationToken);
        await ReportAsync(progress, LibraryScanPhase.Subtitles, 0, episodeIds.Length, cancellationToken);
        foreach (var episodeCandidates in subtitleCandidates.GroupBy(x => x.EpisodeId))
        {
            await ReportAsync(progress, LibraryScanPhase.Subtitles, subtitleEpisodes++, episodeIds.Length, cancellationToken);
            var episodeId = episodeCandidates.Key;
            var mediaCandidates = episodeCandidates
                .OrderBy(x => x.MediaPath, StringComparer.Ordinal)
                .ToArray();

            var sidecar = await subtitleImport.ImportPreferredSidecarAsync(
                episodeId,
                mediaCandidates.Select(x => x.MediaPath).ToArray(),
                sidecarListings,
                cancellationToken);

            if (sidecar.Status == SubtitleSidecarImportStatus.Imported)
            {
                subtitleFiles++;
                continue;
            }

            if (sidecar.Status == SubtitleSidecarImportStatus.Unavailable)
            {
                continue;
            }

            foreach (var candidate in mediaCandidates)
            {
                var sourcePrefix = EmbeddedSubtitleExtractor.BuildSourcePrefix(candidate.MediaPath);

                // Stored stamps keep microseconds while file timestamps carry 100 ns ticks (see
                // SubtitleImportService), so compare at the stored precision.
                var candidateStamp = candidate.SourceUpdatedAt.AddTicks(
                    -(candidate.SourceUpdatedAt.Ticks % TimeSpan.TicksPerMicrosecond));
                var freshEmbeddedCount = embeddedTracks.Count(x =>
                    x.EpisodeId == episodeId &&
                    x.SourceUpdatedAt == candidateStamp &&
                    x.SourceKey.StartsWith(sourcePrefix, StringComparison.Ordinal));

                if (freshEmbeddedCount > 0)
                {
                    subtitleFiles += freshEmbeddedCount;
                    break;
                }

                var embedded = await embeddedSubtitleExtractor.ExtractPreferredTextAsync(
                    candidate.MediaPath,
                    contentLanguage,
                    cancellationToken);

                if (embedded is null)
                {
                    continue;
                }

                await subtitleImport.ImportPreferredContentAsync(
                    episodeId,
                    embedded.SourceKey,
                    embedded.Format,
                    candidate.SourceUpdatedAt,
                    embedded.Content,
                    cancellationToken);
                subtitleFiles++;
                break;
            }
        }

        logger.LogInformation(
            "Library reconciliation completed for {Root}: {Discovered} new, {Updated} updated, {Removed} removed, {Skipped} skipped, {Subtitles} subtitle files, {ArtworkImported} local artwork imported, {ArtworkUnchanged} unchanged, {MetadataWarnings} NFO files ignored, {MediaAnalyzed} media analysed, {MediaAnalysisFailed} media analyses failed, {MediaAnalysisDeferred} deferred, {MediaAnalysisUnchanged} unchanged.",
            root.Path,
            discovered,
            updated,
            removed,
            skipped,
            subtitleFiles,
            localArtworkImported,
            localArtworkUnchanged,
            metadataWarnings,
            inventory.Analyzed,
            inventory.Failed,
            inventory.Deferred,
            inventory.Unchanged);

        await ReportAsync(progress, LibraryScanPhase.Completed, candidates.Count, candidates.Count, cancellationToken);

        return new ScanResult(discovered, updated, skipped, subtitleFiles)
        {
            Removed = removed,
            Relinked = relinked,
            MetadataWarnings = metadataWarnings,
            MediaFiles = candidates.Count,
            ArtworkImported = localArtworkImported,
            Errors = errors,
            Warnings = warnings,
            WarningCount = warningCount,
            MediaInventory = inventory
        };
    }

    private static void EnsureRootStillReadable(string rootPath)
    {
        bool readable;
        try
        {
            readable = Directory.Exists(rootPath) &&
                Directory.EnumerateFileSystemEntries(rootPath).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            readable = false;
        }

        if (!readable)
        {
            throw new IOException(
                "Library root became unavailable during reconciliation; missing media was not removed.");
        }
    }

    // A known file that vanished and a new file that appeared are the same file when their
    // size and modification time match one-to-one and either the stored content fingerprint
    // matches or (without one) the file name is unchanged. Anything less certain is left to
    // the owner: the new file is added, the old one removed, and a warning records it.
    private async Task<RelinkPlan> PlanRelinksAsync(
        IReadOnlyList<MediaFile> vanished,
        IReadOnlyList<FileInfo> appeared,
        CancellationToken cancellationToken)
    {
        if (vanished.Count == 0 || appeared.Count == 0)
        {
            return RelinkPlan.Empty;
        }

        var appearedByIdentity = appeared
            .Where(file => file.Length > 0)
            .GroupBy(file => (file.Length, file.LastWriteTimeUtc))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var candidates = vanished
            .Where(mediaFile => mediaFile.SizeBytes > 0)
            .GroupBy(mediaFile => (mediaFile.SizeBytes, mediaFile.LastWriteTimeUtc))
            .Where(group => appearedByIdentity.ContainsKey(group.Key))
            .ToArray();
        if (candidates.Length == 0)
        {
            return RelinkPlan.Empty;
        }

        var candidateIds = candidates.SelectMany(group => group).Select(x => x.Id).ToArray();
        var fingerprints = await db.MediaAnalyses
            .AsNoTracking()
            .Where(x => candidateIds.Contains(x.MediaFileId) && x.SourceFingerprint != null)
            .ToDictionaryAsync(x => x.MediaFileId, x => x.SourceFingerprint!, cancellationToken);

        var moves = new Dictionary<string, MediaFile>(StringComparer.Ordinal);
        var ambiguous = new List<string>();
        foreach (var group in candidates)
        {
            var newFiles = appearedByIdentity[group.Key];
            var oldFiles = group.ToArray();
            if (oldFiles.Length != 1 || newFiles.Length != 1)
            {
                ambiguous.AddRange(newFiles.Select(file => Path.GetFullPath(file.FullName)));
                continue;
            }

            var old = oldFiles[0];
            var newPath = Path.GetFullPath(newFiles[0].FullName);
            var confirmed = fingerprints.TryGetValue(old.Id, out var stored)
                ? string.Equals(
                    await MediaInventoryService.TryComputeFingerprintAsync(newPath, cancellationToken),
                    stored,
                    StringComparison.Ordinal)
                : string.Equals(Path.GetFileName(old.Path), Path.GetFileName(newPath), StringComparison.Ordinal);

            if (confirmed)
            {
                moves.Add(newPath, old);
            }
            else if (!fingerprints.ContainsKey(old.Id))
            {
                ambiguous.Add(newPath);
            }
        }

        return new RelinkPlan(moves, ambiguous);
    }

    private sealed record RelinkPlan(
        IReadOnlyDictionary<string, MediaFile> Moves,
        IReadOnlyList<string> Ambiguous)
    {
        public static readonly RelinkPlan Empty = new(
            new Dictionary<string, MediaFile>(StringComparer.Ordinal),
            []);
    }

    // The scope must stay inside the root; a relative folder is never allowed to escape it.
    private static string ResolveScopePath(string rootPath, string? relativeFolder)
    {
        if (relativeFolder is null)
        {
            return rootPath;
        }

        var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var scopePath = Path.GetFullPath(Path.Combine(rootPath, relativeFolder));
        if (Path.IsPathRooted(relativeFolder) ||
            !scopePath.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The folder to scan must be a relative path inside the library root.",
                nameof(relativeFolder));
        }

        return scopePath;
    }

    private static Task ReportAsync(
        LibraryScanProgressHandler? progress,
        LibraryScanPhase phase,
        int processed,
        int total,
        CancellationToken cancellationToken) =>
        progress is null
            ? Task.CompletedTask
            : progress(new LibraryScanProgress(phase, processed, total), cancellationToken);

    private static void AddWarning(
        List<ScanWarning> warnings,
        ref int warningCount,
        string rootPath,
        string path,
        string reason)
    {
        warningCount++;
        if (warnings.Count < ScanResult.MaxRecordedWarnings)
        {
            warnings.Add(new ScanWarning(reason, ToRelativePath(rootPath, path)));
        }
    }

    internal static string ToRelativePath(string rootPath, string path) =>
        Path.GetRelativePath(rootPath, path).Replace('\\', '/');

    // Precedence: an episode NFO whose numbers agree with the file name > the file-name title.
    // The first agreeing NFO (ordinal media path order) wins when several files share an episode.
    // A rejected NFO returns null so the current title is kept instead of flapping on a bad read.
    private string? ResolveEpisodeTitle(
        string? nfoPath,
        MediaDescriptor descriptor,
        bool alreadyTitledFromNfo,
        ref int metadataWarnings,
        out bool titleFromNfo,
        out bool nfoRejected)
    {
        titleFromNfo = false;
        nfoRejected = false;
        if (alreadyTitledFromNfo)
        {
            return null;
        }

        if (nfoPath is null)
        {
            return descriptor.EpisodeTitle;
        }

        var nfo = NfoReader.ReadEpisodes(nfoPath);
        if (nfo.Value is null)
        {
            metadataWarnings++;
            nfoRejected = true;
            LogRejectedNfo(nfoPath, nfo.Warning);
            return null;
        }

        var entry = nfo.Value.FirstOrDefault(x =>
            x.Describes(descriptor.SeasonNumber, descriptor.EpisodeNumber));
        if (entry is null)
        {
            logger.LogDebug(
                "Episode NFO {Path} describes different season/episode numbers than its media file name; the file name stays authoritative.",
                nfoPath);
            return descriptor.EpisodeTitle;
        }

        if (entry.Title is null)
        {
            return descriptor.EpisodeTitle;
        }

        titleFromNfo = true;
        return entry.Title;
    }

    // An AniList or MyAnimeList ID from tvshow.nfo replaces the fuzzy title search for a newly
    // discovered anime. It never replaces an existing match; a rejected ID falls through to
    // automatic title matching.
    private async Task MatchNfoProviderIdAsync(
        AnimeMetadataService metadata,
        Guid animeId,
        string? aniListId,
        string? myAnimeListId,
        CancellationToken cancellationToken)
    {
        var result = await metadata.MatchNfoProviderIdsAsync(
            animeId,
            aniListId,
            myAnimeListId,
            cancellationToken);
        if (result.Success)
        {
            logger.LogInformation(
                "Matched anime {AnimeId} from its local tvshow.nfo provider ID.",
                animeId);
            return;
        }

        logger.LogWarning(
            "The provider ID from the local tvshow.nfo of anime {AnimeId} was not applied: {Error} Automatic title matching continues.",
            animeId,
            result.Error);
    }

    // Persists the plot/overview, original title, year/premiered date and additional provider IDs
    // a tvshow.nfo carries. This is the one canonical place for that local data: AnimeMetadata
    // (Features/Metadata) belongs to a matched provider, so NFO facts never masquerade as it.
    private static void ApplyLocalMetadata(
        AppDbContext db,
        Dictionary<Guid, AnimeLocalMetadata> localMetadataByAnimeId,
        Guid animeId,
        NfoShowMetadata show,
        FileInfo showNfoFile)
    {
        if (!localMetadataByAnimeId.TryGetValue(animeId, out var local))
        {
            local = new AnimeLocalMetadata { AnimeId = animeId };
            localMetadataByAnimeId.Add(animeId, local);
            db.AnimeLocalMetadata.Add(local);
        }

        local.OriginalTitle = show.OriginalTitle;
        local.Plot = show.Plot;
        local.Year = show.Year;
        local.Premiered = show.Premiered;
        local.MyAnimeListId = show.ProviderIds.MyAnimeList;
        local.TvdbId = show.ProviderIds.Tvdb;
        local.TmdbId = show.ProviderIds.Tmdb;
        local.ImdbId = show.ProviderIds.Imdb;
        local.SourceFileSizeBytes = showNfoFile.Length;
        local.SourceFileLastWriteTimeUtc = showNfoFile.LastWriteTimeUtc;
        local.UpdatedAt = DateTime.UtcNow;
    }

    private void LogRejectedNfo(string path, string? reason) =>
        logger.LogWarning(
            "Ignored local NFO metadata {Path}: {Reason} The library scan continues without it.",
            path,
            reason);

    private sealed record SubtitleCandidate(
        Guid EpisodeId,
        string MediaPath,
        DateTime SourceUpdatedAt);

    private sealed record ExistingEmbeddedTrack(
        Guid EpisodeId,
        string SourceKey,
        DateTime SourceUpdatedAt);

    private static string? TryGetAnimeDirectory(
        string rootPath,
        string mediaPath)
    {
        var relative = Path.GetRelativePath(rootPath, mediaPath);
        var parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return null;
        }

        var directory = Path.GetFullPath(Path.Combine(rootPath, parts[0]));
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return directory.StartsWith(normalizedRoot, StringComparison.Ordinal)
            ? directory
            : null;
    }
}
