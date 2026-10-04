using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Subtitles;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Naming;

// Plan -> preview -> execute for renaming an anime's existing files (and optionally its series
// folder). Every move is checked against Sonarr ownership (SonarrParallelSafety.CanRename),
// read-only mounts, cross-device moves, existing targets and library identity before anything
// changes. Execution runs as an Operation, rolls filesystem moves back on failure and updates the
// path-based canonical records (media files, subtitle sources, anime key) in one database
// transaction, with the ownership and SABnzbd acquisition stores rekeyed alongside it, so
// progress and episode identity survive without a rescan.
public sealed class AnimeRenameService(
    AppDbContext db,
    AnimeNamingProfileStore namingStore,
    AcquisitionOwnershipStore ownershipStore,
    SabnzbdAcquisitionStore acquisitionStore,
    AnimeMonitoringStore monitoringStore,
    AnimeImportStore importStore,
    SonarrObservationService observation,
    OperationRunner operations,
    AnimeRenameFileSystem fileSystem,
    ILogger<AnimeRenameService> logger)
{
    public const string OperationKind = "anime-rename";
    private const string LogModule = "Rename";
    // Acquisition imports move files into series folders and rescan them, so they conflict too.
    internal static readonly string[] ConflictingOperationKinds = [LibraryScanCoordinator.OperationKind, OperationKind, AnimeImportExecutor.OperationKind];
    private static readonly string[] SidecarDirectoryNames = ["Subs", "Subtitles"];

    public async Task<AnimeRenamePlan?> PlanAsync(
        Guid animeId,
        bool renameSeriesFolder,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await observation.GetSnapshotAsync(forceRefresh: false, cancellationToken);
        return await PlanAsync(animeId, renameSeriesFolder, snapshot, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<AnimeRenamePlan?> PlanAsync(
        Guid animeId,
        bool renameSeriesFolder,
        AcquisitionOwnershipSnapshot ownership,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        var anime = await db.Anime.AsNoTracking().SingleOrDefaultAsync(x => x.Id == animeId, cancellationToken);
        if (anime is null)
        {
            return null;
        }

        var metadata = await db.AnimeMetadata.AsNoTracking().SingleOrDefaultAsync(x => x.AnimeId == animeId, cancellationToken);
        var episodes = await db.Episodes.AsNoTracking().Where(x => x.AnimeId == animeId).ToListAsync(cancellationToken);
        var episodeIds = episodes.Select(x => x.Id).ToArray();
        var mediaFiles = await db.MediaFiles.AsNoTracking()
            .Where(x => x.EpisodeId.HasValue && episodeIds.Contains(x.EpisodeId.Value))
            .ToListAsync(cancellationToken);
        mediaFiles.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        var rootIds = mediaFiles.Select(x => x.LibraryRootId).Distinct().ToArray();
        var roots = await db.LibraryRoots.AsNoTracking()
            .Where(x => rootIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var namingState = await namingStore.LoadAsync(cancellationToken);

        var primaryRootId = mediaFiles
            .GroupBy(x => x.LibraryRootId)
            .OrderByDescending(group => group.Count())
            .Select(group => (Guid?)group.Key)
            .FirstOrDefault();
        var primaryNaming = AnimeNamingProfileStore.Resolve(namingState, animeId, primaryRootId);

        var episodeById = episodes.ToDictionary(x => x.Id);
        var episodeByNumber = episodes.ToDictionary(x => (x.SeasonNumber, x.Number));
        var absoluteOffsets = LocalAbsoluteOffsets(episodes);
        var blocking = new List<string>();
        var folderMoves = new List<AnimeRenameMove>();
        var drafts = new List<Draft>();
        var seasonArtworkDirectories = new HashSet<string>(StringComparer.Ordinal);
        var seriesByFolder = new Dictionary<string, AnimeNamingSeries>(StringComparer.Ordinal);
        string? targetKey = renameSeriesFolder ? null : anime.Key;

        foreach (var mediaFile in mediaFiles)
        {
            var episode = episodeById[mediaFile.EpisodeId!.Value];
            var source = Path.GetFullPath(mediaFile.Path);
            if (!roots.TryGetValue(mediaFile.LibraryRootId, out var root))
            {
                drafts.Add(Draft.Blocked(mediaFile, episode, source, "The library root of this file no longer exists."));
                continue;
            }

            var rootPath = Path.GetFullPath(root.Path);
            var relativeParts = Path.GetRelativePath(rootPath, source)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (relativeParts.Length < 2 || relativeParts[0] == "..")
            {
                drafts.Add(Draft.Blocked(mediaFile, episode, source, "The file is not inside a series folder of its library root."));
                continue;
            }

            if (!File.Exists(source))
            {
                drafts.Add(Draft.Blocked(mediaFile, episode, source, "The file no longer exists; rescan the library first."));
                continue;
            }

            var naming = AnimeNamingProfileStore.Resolve(namingState, animeId, root.Id);
            var profile = naming.Profile;
            var currentSeriesFolder = Path.Combine(rootPath, relativeParts[0]);
            if (!seriesByFolder.TryGetValue(currentSeriesFolder, out var series))
            {
                series = BuildSeries(anime, metadata, currentSeriesFolder, naming.SeriesType);
                seriesByFolder[currentSeriesFolder] = series;
            }

            var seriesFolderName = renameSeriesFolder
                ? AnimeNamingFormatter.BuildSeriesFolderName(profile, series)
                : relativeParts[0];
            var targetSeriesFolder = Path.Combine(rootPath, seriesFolderName);
            if (renameSeriesFolder &&
                !string.Equals(currentSeriesFolder, targetSeriesFolder, StringComparison.Ordinal) &&
                !folderMoves.Any(move => move.SourcePath == currentSeriesFolder))
            {
                folderMoves.Add(new AnimeRenameMove(currentSeriesFolder, targetSeriesFolder));
            }

            var release = AnimeReleaseParser.TryParse(Path.GetFileName(source), out var parsed) ? parsed : null;
            var namingEpisodes = BuildNamingEpisodes(episode, release, episodeByNumber, absoluteOffsets);
            var seasonFolder = AnimeNamingFormatter.BuildSeasonFolderName(profile, series, episode.SeasonNumber);
            var fileName = AnimeNamingFormatter.BuildEpisodeFileName(
                profile,
                new AnimeNamingRequest(series, namingEpisodes, release)) + Path.GetExtension(source);
            var targetDirectory = seasonFolder is null ? targetSeriesFolder : Path.Combine(targetSeriesFolder, seasonFolder);
            var target = Path.GetFullPath(Path.Combine(targetDirectory, fileName));

            var nameProblem = NameProblem(seriesFolderName, seasonFolder, fileName);
            if (nameProblem is not null)
            {
                drafts.Add(Draft.Blocked(mediaFile, episode, source, nameProblem, target));
                continue;
            }

            if (!MediaPathParser.TryParse(rootPath, target, out var descriptor) ||
                descriptor.SeasonNumber != episode.SeasonNumber ||
                descriptor.EpisodeNumber != episode.Number)
            {
                var parsedAs = descriptor is null ? "nothing" : $"S{descriptor.SeasonNumber:00}E{descriptor.EpisodeNumber:00}";
                drafts.Add(Draft.Blocked(
                    mediaFile,
                    episode,
                    source,
                    $"The new name would be scanned as {parsedAs} instead of S{episode.SeasonNumber:00}E{episode.Number:00}; include {{season}}/{{episode}} or keep season folders so the library keeps this episode's identity.",
                    target));
                continue;
            }

            targetKey ??= descriptor.AnimeKey;
            if (!string.Equals(descriptor.AnimeKey, targetKey, StringComparison.Ordinal))
            {
                drafts.Add(Draft.Blocked(mediaFile, episode, source, "The new series folder would split this anime into several library entries.", target));
                continue;
            }

            var sidecars = FindSidecars(source, targetDirectory, Path.GetFileNameWithoutExtension(fileName));
            var sourceDirectory = Path.GetDirectoryName(source)!;
            if (!string.Equals(sourceDirectory, currentSeriesFolder, StringComparison.Ordinal) &&
                !string.Equals(Rebase(sourceDirectory, folderMoves), Path.GetDirectoryName(target), StringComparison.Ordinal) &&
                seasonArtworkDirectories.Add(sourceDirectory))
            {
                sidecars = [.. sidecars, .. FindSeasonArtwork(sourceDirectory, Path.GetDirectoryName(target)!, folderMoves)];
            }

            drafts.Add(new Draft(mediaFile, episode, source, target, currentSeriesFolder, sidecars, null));
        }

        targetKey ??= anime.Key;
        if (!string.Equals(targetKey, anime.Key, StringComparison.Ordinal))
        {
            if (await db.Anime.AnyAsync(x => x.Key == targetKey && x.Id != animeId, cancellationToken))
            {
                blocking.Add($"Another anime already uses the library key '{targetKey}' that the new series folder would produce.");
            }

            if (ownership.State.Anime.ContainsKey(targetKey))
            {
                blocking.Add($"Sonarr migration settings already exist for '{targetKey}'; resolve them before renaming the series folder.");
            }
        }

        foreach (var move in folderMoves)
        {
            if (TargetOccupied(move.SourcePath, move.TargetPath))
            {
                blocking.Add($"The series folder '{move.TargetPath}' already exists.");
            }

            if (folderMoves.Count(other => string.Equals(other.TargetPath, move.TargetPath, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                blocking.Add($"Several series folders would be merged into '{move.TargetPath}'; merge them manually first.");
            }

            var decision = SonarrParallelSafety.CanRename(ownership, anime.Key, move.SourcePath, move.TargetPath, now);
            if (!decision.Allowed)
            {
                blocking.Add($"Series folder: {decision.Reason}");
            }
        }

        var items = CheckItems(drafts, folderMoves, anime.Key, ownership, now);
        blocking.AddRange(CheckWritableVolumes(items, folderMoves));
        blocking.AddRange(await CheckActiveLibraryOperationsAsync(cancellationToken));

        return new AnimeRenamePlan(
            anime.Id,
            anime.Title,
            anime.Key,
            targetKey,
            primaryNaming,
            renameSeriesFolder,
            folderMoves,
            items,
            blocking.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<AnimeRenameResult> ExecuteAsync(
        Guid animeId,
        bool renameSeriesFolder,
        string expectedFingerprint,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await observation.GetSnapshotAsync(forceRefresh: true, cancellationToken);
        return await ExecuteAsync(animeId, renameSeriesFolder, expectedFingerprint, snapshot, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<AnimeRenameResult> ExecuteAsync(
        Guid animeId,
        bool renameSeriesFolder,
        string expectedFingerprint,
        AcquisitionOwnershipSnapshot ownership,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The plan is always rebuilt from current state; client input only selects and confirms it.
        var plan = await PlanAsync(animeId, renameSeriesFolder, ownership, now, cancellationToken);
        if (plan is null)
        {
            return new(false, "Anime not found.", null, []);
        }

        if (!string.Equals(plan.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return new(false, "The library, naming profile or Sonarr state changed since the preview. Review the updated preview and confirm again.", null, []);
        }

        if (!plan.CanExecute)
        {
            var reason = plan.BlockingReasons.Count > 0
                ? string.Join(" ", plan.BlockingReasons)
                : plan.RenameSeriesFolder && plan.BlockedCount > 0
                    ? "Resolve the blocked files before moving the series folder."
                    : "Nothing to rename.";
            return new(false, $"Nothing was renamed. {reason}", null, []);
        }

        Guid? operationId = null;
        var summary = plan.FolderMoves.Count > 0
            ? $"Renamed {plan.RenameCount} file(s) and moved {plan.FolderMoves.Count} series folder(s)."
            : $"Renamed {plan.RenameCount} file(s).";
        try
        {
            var moves = await operations.RunAsync(
                new OperationDescriptor(
                    OperationKind,
                    "Library",
                    $"Rename files: {plan.AnimeTitle}",
                    Subject: plan.AnimeKey,
                    Retryable: false),
                async (operation, token) =>
                {
                    operationId = operation.OperationId;
                    return await ExecutePlanAsync(plan, ownership, operation, token);
                },
                summary,
                cancellationToken);

            return new(true, summary, operationId, moves);
        }
        catch (AnimeRenameException exception)
        {
            return new(false, exception.Message, operationId, []);
        }
    }

    private async Task<IReadOnlyList<AnimeRenameMove>> ExecutePlanAsync(
        AnimeRenamePlan plan,
        AcquisitionOwnershipSnapshot ownership,
        OperationExecutionContext operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await operation.LogAsync(
            OperationLogLevel.Information,
            LogModule,
            $"Profile '{plan.Naming.Profile.Name}': {plan.RenameCount} file(s), {plan.FolderMoves.Count} series folder(s), {plan.BlockedCount} blocked file(s) skipped.",
            CancellationToken.None);

        var journal = new List<AnimeRenameMove>();
        var createdDirectories = new List<string>();
        var renamed = plan.Items.Where(item => item.Status == AnimeRenameItemStatus.Rename).ToArray();
        var fileMoves = new List<AnimeRenameMove>();

        try
        {
            foreach (var folder in plan.FolderMoves)
            {
                MoveEntry(folder.SourcePath, folder.TargetPath, directory: true, journal);
            }

            foreach (var item in renamed)
            {
                var mediaSource = Rebase(item.SourcePath, plan.FolderMoves);
                MoveFileInto(mediaSource, item.TargetPath, journal, createdDirectories);
                fileMoves.Add(new AnimeRenameMove(item.SourcePath, item.TargetPath));

                foreach (var sidecar in item.Sidecars)
                {
                    MoveFileInto(Rebase(sidecar.SourcePath, plan.FolderMoves), sidecar.TargetPath, journal, createdDirectories);
                    fileMoves.Add(sidecar);
                }
            }
        }
        catch (Exception exception)
        {
            var failures = RollBack(journal, createdDirectories);
            await LogRollbackAsync(operation, exception, journal.Count, failures);
            throw new AnimeRenameException(RollbackMessage(exception, journal.Count, failures), exception);
        }

        foreach (var move in plan.FolderMoves.Concat(fileMoves))
        {
            await operation.LogAsync(OperationLogLevel.Information, LogModule, $"Renamed '{move.SourcePath}' -> '{move.TargetPath}'.", CancellationToken.None);
        }

        try
        {
            await CommitRecordsAsync(plan, renamed);
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            var failures = RollBack(journal, createdDirectories);
            await LogRollbackAsync(operation, exception, journal.Count, failures);
            throw new AnimeRenameException(RollbackMessage(exception, journal.Count, failures), exception);
        }

        RemoveEmptySourceDirectories(renamed, plan.FolderMoves);
        await operation.LogAsync(
            OperationLogLevel.Information,
            LogModule,
            plan.TargetAnimeKey == plan.AnimeKey
                ? "Library records updated; episode identity and progress are unchanged."
                : $"Library records updated; anime key '{plan.AnimeKey}' is now '{plan.TargetAnimeKey}'. Episode identity and progress are unchanged.",
            CancellationToken.None);

        return plan.FolderMoves.Concat(fileMoves).ToArray();
    }

    // Media paths, subtitle sources and the anime key are the library's path-derived identity.
    // They change in one transaction together with the ownership store so a later scan sees the
    // same anime/episodes instead of deleting and re-creating them (which would drop progress).
    private async Task CommitRecordsAsync(
        AnimeRenamePlan plan,
        IReadOnlyList<AnimeRenamePlanItem> renamed)
    {
        var mediaMap = renamed.ToDictionary(item => item.SourcePath, item => item.TargetPath, StringComparer.Ordinal);
        var sidecarMap = renamed
            .SelectMany(item => item.Sidecars)
            .ToDictionary(move => move.SourcePath, move => move.TargetPath, StringComparer.Ordinal);

        string MapPath(string path)
        {
            var full = Path.GetFullPath(path);
            if (mediaMap.TryGetValue(full, out var mediaTarget))
            {
                return mediaTarget;
            }

            if (sidecarMap.TryGetValue(full, out var sidecarTarget))
            {
                return sidecarTarget;
            }

            return Rebase(full, plan.FolderMoves);
        }

        var mediaIds = plan.Items.Select(item => item.MediaFileId).ToArray();
        var episodeIds = plan.Items.Select(item => item.EpisodeId).Distinct().ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);

        var mediaFiles = await db.MediaFiles.Where(x => mediaIds.Contains(x.Id)).ToListAsync(CancellationToken.None);
        foreach (var mediaFile in mediaFiles)
        {
            var mapped = MapPath(mediaFile.Path);
            if (!string.Equals(mapped, mediaFile.Path, StringComparison.Ordinal))
            {
                mediaFile.Path = mapped;
            }
        }

        var tracks = await db.SubtitleTracks.Where(x => episodeIds.Contains(x.EpisodeId)).ToListAsync(CancellationToken.None);
        foreach (var track in tracks)
        {
            track.Path = MapSubtitleSource(track.Path, MapPath);
        }

        if (!string.Equals(plan.TargetAnimeKey, plan.AnimeKey, StringComparison.Ordinal))
        {
            var anime = await db.Anime.SingleAsync(x => x.Id == plan.AnimeId, CancellationToken.None);
            anime.Key = plan.TargetAnimeKey;
        }

        await db.SaveChangesAsync(CancellationToken.None);

        // The JSON acquisition stores cannot join the database transaction, so every applied
        // store change registers its reversal and is undone when a later step or the commit fails.
        var compensations = new List<Func<Task>>();
        try
        {
            var ownershipChanged = false;
            await ownershipStore.UpdateAsync(
                state =>
                {
                    var updated = RekeyOwnership(state, plan.AnimeKey, plan.TargetAnimeKey, MapPath);
                    ownershipChanged = !ReferenceEquals(updated, state);
                    return updated;
                },
                CancellationToken.None);
            if (ownershipChanged)
            {
                var reverse = BuildReverseMap(mediaMap, sidecarMap, plan.FolderMoves);
                compensations.Add(() => ownershipStore.UpdateAsync(
                    state => RekeyOwnership(state, plan.TargetAnimeKey, plan.AnimeKey, reverse),
                    CancellationToken.None));
            }

            if (await acquisitionStore.RekeyAnimeAsync(plan.AnimeKey, plan.TargetAnimeKey, CancellationToken.None))
            {
                compensations.Add(() => acquisitionStore.RekeyAnimeAsync(plan.TargetAnimeKey, plan.AnimeKey, CancellationToken.None));
            }

            if (await monitoringStore.RekeyAnimeAsync(plan.AnimeKey, plan.TargetAnimeKey, CancellationToken.None))
            {
                compensations.Add(() => monitoringStore.RekeyAnimeAsync(plan.TargetAnimeKey, plan.AnimeKey, CancellationToken.None));
            }

            if (await importStore.RekeyAnimeAsync(plan.AnimeKey, plan.TargetAnimeKey, MapPath, CancellationToken.None))
            {
                var reverseImports = BuildReverseMap(mediaMap, sidecarMap, plan.FolderMoves);
                compensations.Add(() => importStore.RekeyAnimeAsync(plan.TargetAnimeKey, plan.AnimeKey, reverseImports, CancellationToken.None));
            }

            await transaction.CommitAsync(CancellationToken.None);
        }
        catch
        {
            for (var index = compensations.Count - 1; index >= 0; index--)
            {
                try
                {
                    await compensations[index]();
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    logger.LogError(exception, "Could not undo an acquisition store change after the failed rename of {AnimeKey}.", plan.AnimeKey);
                }
            }

            throw;
        }
    }

    // Moves the anime's migration assignment, jobs and owned paths to the new key/paths.
    public static AcquisitionOwnershipState RekeyOwnership(
        AcquisitionOwnershipState state,
        string oldKey,
        string newKey,
        Func<string, string> mapPath)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mapPath);

        var changed = false;
        var anime = new Dictionary<string, AnimeManagementAssignment>(state.Anime, StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(oldKey, newKey, StringComparison.Ordinal) && anime.Remove(oldKey, out var assignment))
        {
            anime[newKey] = assignment with { AnimeKey = newKey };
            changed = true;
        }

        var jobs = new Dictionary<string, AcquisitionOwnership>(state.Jobs, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, job) in state.Jobs)
        {
            if (!string.Equals(oldKey, newKey, StringComparison.Ordinal) &&
                job.AnimeKey.Equals(oldKey, StringComparison.OrdinalIgnoreCase))
            {
                jobs[id] = job with { AnimeKey = newKey };
                changed = true;
            }
        }

        var paths = new Dictionary<string, ManagedMediaPath>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, path) in state.Paths)
        {
            var mapped = SonarrParallelSafety.NormalizePath(mapPath(path.Path));
            var mappedKey = path.AnimeKey.Equals(oldKey, StringComparison.OrdinalIgnoreCase) ? newKey : path.AnimeKey;
            if (!string.Equals(mapped, path.Path, StringComparison.Ordinal) ||
                !string.Equals(mappedKey, path.AnimeKey, StringComparison.Ordinal))
            {
                changed = true;
                paths[mapped] = path with { Path = mapped, AnimeKey = mappedKey };
            }
            else
            {
                paths[key] = path;
            }
        }

        return changed ? state with { Anime = anime, Jobs = jobs, Paths = paths } : state;
    }

    private IReadOnlyList<AnimeRenamePlanItem> CheckItems(
        IReadOnlyList<Draft> drafts,
        IReadOnlyList<AnimeRenameMove> folderMoves,
        string animeKey,
        AcquisitionOwnershipSnapshot ownership,
        DateTimeOffset now)
    {
        var targetCounts = drafts
            .Where(draft => draft.Reason is null && draft.Target != draft.Source)
            .SelectMany(draft => draft.Sidecars.Select(sidecar => sidecar.TargetPath).Prepend(draft.Target))
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var items = new List<AnimeRenamePlanItem>();
        foreach (var draft in drafts)
        {
            if (draft.Reason is not null)
            {
                items.Add(draft.ToItem(AnimeRenameItemStatus.Blocked, draft.Reason));
                continue;
            }

            if (string.Equals(draft.Source, draft.Target, StringComparison.Ordinal))
            {
                items.Add(draft.ToItem(AnimeRenameItemStatus.Unchanged, null));
                continue;
            }

            var reason = CheckMove(draft, folderMoves, animeKey, ownership, now, targetCounts);
            items.Add(reason is null
                ? draft.ToItem(Rebase(draft.Source, folderMoves) == draft.Target && draft.Sidecars.All(sidecar => Rebase(sidecar.SourcePath, folderMoves) == sidecar.TargetPath)
                    ? AnimeRenameItemStatus.Unchanged
                    : AnimeRenameItemStatus.Rename, null)
                : draft.ToItem(AnimeRenameItemStatus.Blocked, reason));
        }

        return items;
    }

    private string? CheckMove(
        Draft draft,
        IReadOnlyList<AnimeRenameMove> folderMoves,
        string animeKey,
        AcquisitionOwnershipSnapshot ownership,
        DateTimeOffset now,
        IReadOnlyDictionary<string, int> targetCounts)
    {
        var moves = draft.Sidecars.Prepend(new AnimeRenameMove(draft.Source, draft.Target)).ToArray();
        foreach (var move in moves)
        {
            if (targetCounts.GetValueOrDefault(move.TargetPath) > 1)
            {
                return $"Another file in this plan would also be named '{Path.GetFileName(move.TargetPath)}'.";
            }

            // Occupancy is checked where the target lives before a series-folder move happens.
            var targetNow = Unrebase(move.TargetPath, folderMoves);
            if (TargetOccupied(move.SourcePath, targetNow))
            {
                return $"'{move.TargetPath}' already exists.";
            }

            var decision = SonarrParallelSafety.CanRename(ownership, animeKey, move.SourcePath, move.TargetPath, now);
            if (!decision.Allowed)
            {
                return decision.Reason;
            }

            var sourceVolume = fileSystem.GetVolumeKey(Path.GetDirectoryName(move.SourcePath)!);
            var targetVolume = fileSystem.GetVolumeKey(NearestExistingDirectory(Path.GetDirectoryName(targetNow)!));
            if (!string.Equals(sourceVolume, targetVolume, StringComparison.Ordinal))
            {
                return $"'{move.TargetPath}' is on another filesystem ({targetVolume}); Jularr only renames within one filesystem so a move can never be a partial copy.";
            }
        }

        return null;
    }

    // A scan that enumerated the old paths would treat the renamed files as removed and re-add
    // the old ones; a second rename would race this one. Both must finish first.
    private async Task<IReadOnlyList<string>> CheckActiveLibraryOperationsAsync(CancellationToken cancellationToken)
    {
        var active = await new OperationStore(db).ListAsync(
            new OperationListFilter(View: "active", Category: "Library"),
            cancellationToken);
        return active
            .Where(operation => ConflictingOperationKinds.Contains(operation.Kind, StringComparer.Ordinal))
            .Select(operation => $"'{operation.Title}' is {operation.Status.ToString().ToLowerInvariant()}; rename after it finishes.")
            .ToArray();
    }

    private IEnumerable<string> CheckWritableVolumes(
        IReadOnlyList<AnimeRenamePlanItem> items,
        IReadOnlyList<AnimeRenameMove> folderMoves)
    {
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var folder in folderMoves)
        {
            directories.Add(Path.GetDirectoryName(folder.SourcePath)!);
        }

        foreach (var item in items.Where(item => item.Status == AnimeRenameItemStatus.Rename))
        {
            foreach (var move in item.Sidecars.Prepend(new AnimeRenameMove(item.SourcePath, item.TargetPath)))
            {
                directories.Add(Path.GetDirectoryName(move.SourcePath)!);
                directories.Add(NearestExistingDirectory(Path.GetDirectoryName(Unrebase(move.TargetPath, folderMoves))!));
            }
        }

        foreach (var directory in directories)
        {
            var blocker = fileSystem.GetWriteBlocker(directory);
            if (blocker is not null)
            {
                yield return $"{blocker} The library may be mounted read-only; nothing will be renamed.";
            }
        }
    }

    private void MoveFileInto(
        string source,
        string target,
        List<AnimeRenameMove> journal,
        List<string> createdDirectories)
    {
        EnsureDirectory(Path.GetDirectoryName(target)!, createdDirectories);
        MoveEntry(source, target, directory: false, journal);
    }

    // A case-only rename goes through a temporary name so it also works on case-insensitive
    // filesystems (SMB/NTFS/APFS) where the target "exists" as the source itself.
    private void MoveEntry(string source, string target, bool directory, List<AnimeRenameMove> journal)
    {
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return;
        }

        if (TargetOccupied(source, target))
        {
            throw new IOException($"'{target}' appeared while renaming.");
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".jularr-rename-{Guid.NewGuid():N}");
            Move(source, temporary, directory, journal);
            Move(temporary, target, directory, journal);
            return;
        }

        Move(source, target, directory, journal);
    }

    private void Move(string source, string target, bool directory, List<AnimeRenameMove> journal)
    {
        if (directory)
        {
            fileSystem.MoveDirectory(source, target);
        }
        else
        {
            fileSystem.MoveFile(source, target);
        }

        journal.Add(new AnimeRenameMove(source, target));
    }

    private static void EnsureDirectory(string directory, List<string> createdDirectories)
    {
        var missing = new Stack<string>();
        var current = directory;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new IOException($"'{directory}' has no existing parent.");
        }

        while (missing.Count > 0)
        {
            var next = missing.Pop();
            Directory.CreateDirectory(next);
            createdDirectories.Add(next);
        }
    }

    // Undoes completed moves in reverse order; returns the moves that could not be undone.
    private IReadOnlyList<AnimeRenameMove> RollBack(List<AnimeRenameMove> journal, List<string> createdDirectories)
    {
        var failures = new List<AnimeRenameMove>();
        for (var index = journal.Count - 1; index >= 0; index--)
        {
            var move = journal[index];
            try
            {
                if (Directory.Exists(move.TargetPath))
                {
                    fileSystem.MoveDirectory(move.TargetPath, move.SourcePath);
                }
                else
                {
                    fileSystem.MoveFile(move.TargetPath, move.SourcePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Rollback could not move {Target} back to {Source}.", move.TargetPath, move.SourcePath);
                failures.Add(move);
            }
        }

        for (var index = createdDirectories.Count - 1; index >= 0; index--)
        {
            TryDeleteEmptyDirectory(createdDirectories[index]);
        }

        journal.Clear();
        return failures;
    }

    private static async Task LogRollbackAsync(
        OperationExecutionContext operation,
        Exception exception,
        int completedMoves,
        IReadOnlyList<AnimeRenameMove> failures)
    {
        await operation.LogAsync(
            OperationLogLevel.Error,
            LogModule,
            $"Rename failed ({exception.Message}); rolling back {completedMoves} completed move(s).",
            CancellationToken.None);

        foreach (var failure in failures)
        {
            await operation.LogAsync(
                OperationLogLevel.Error,
                LogModule,
                $"Rollback failed: move '{failure.TargetPath}' back to '{failure.SourcePath}' manually.",
                CancellationToken.None);
        }
    }

    private static string RollbackMessage(Exception exception, int completedMoves, IReadOnlyList<AnimeRenameMove> failures) =>
        failures.Count == 0
            ? $"Rename failed ({exception.Message}). All {completedMoves} completed move(s) were rolled back; the library is unchanged."
            : $"Rename failed ({exception.Message}). {failures.Count} of {completedMoves} move(s) could not be rolled back; see the operation log for the exact paths.";

    // Removes season/sidecar folders left empty by the rename; series folders are never removed.
    private static void RemoveEmptySourceDirectories(
        IReadOnlyList<AnimeRenamePlanItem> renamed,
        IReadOnlyList<AnimeRenameMove> folderMoves)
    {
        var directories = renamed
            .SelectMany(item => item.Sidecars
                .Select(sidecar => sidecar.SourcePath)
                .Prepend(item.SourcePath)
                .Select(path => (
                    Directory: Path.GetDirectoryName(Rebase(path, folderMoves))!,
                    SeriesFolder: Rebase(item.SourceSeriesFolder, folderMoves))))
            .Distinct()
            .OrderByDescending(entry => entry.Directory.Length);

        foreach (var (directory, seriesFolder) in directories)
        {
            if (directory.StartsWith(seriesFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                TryDeleteEmptyDirectory(directory);
            }
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string MapSubtitleSource(string path, Func<string, string> mapPath)
    {
        foreach (var prefix in new[] { EmbeddedSubtitleExtractor.SourcePrefix, EmbeddedSubtitleExtractor.TranscriptionSourcePrefix })
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var hash = path.IndexOf('#', prefix.Length);
                if (hash < 0)
                {
                    return path;
                }

                var mediaPath = path[prefix.Length..hash];
                return prefix + mapPath(mediaPath) + path[hash..];
            }
        }

        return Path.IsPathFullyQualified(path) ? mapPath(path) : path;
    }

    private static Func<string, string> BuildReverseMap(
        IReadOnlyDictionary<string, string> mediaMap,
        IReadOnlyDictionary<string, string> sidecarMap,
        IReadOnlyList<AnimeRenameMove> folderMoves)
    {
        var reverse = mediaMap.Concat(sidecarMap).ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        return path =>
        {
            var full = Path.GetFullPath(path);
            return reverse.TryGetValue(full, out var original) ? original : Unrebase(full, folderMoves);
        };
    }

    // True when the target path is taken by an entry other than the source itself. On a
    // case-insensitive filesystem a case-only rename sees the source under the target name, so
    // only an exact directory entry with the target name counts as a conflict there.
    private static bool TargetOccupied(string source, string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return false;
        }

        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(target)!;
        var targetName = Path.GetFileName(target);
        var sourceName = Path.GetFileName(source);
        return Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .Any(name => string.Equals(name, targetName, StringComparison.Ordinal) &&
                         !string.Equals(name, sourceName, StringComparison.Ordinal));
    }

    private static string NearestExistingDirectory(string directory)
    {
        var current = directory;
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                return directory;
            }

            current = parent;
        }

        return current;
    }

    private static string Rebase(string path, IReadOnlyList<AnimeRenameMove> folderMoves)
    {
        foreach (var move in folderMoves)
        {
            if (path.StartsWith(move.SourcePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return move.TargetPath + path[move.SourcePath.Length..];
            }
        }

        return path;
    }

    private static string Unrebase(string path, IReadOnlyList<AnimeRenameMove> folderMoves)
    {
        foreach (var move in folderMoves)
        {
            if (path.StartsWith(move.TargetPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return move.SourcePath + path[move.TargetPath.Length..];
            }
        }

        return path;
    }

    private static string? NameProblem(string seriesFolder, string? seasonFolder, string fileName)
    {
        foreach (var name in new[] { seriesFolder, seasonFolder, fileName })
        {
            if (name is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)) || name is "." or "..")
            {
                return "The naming profile renders an empty folder or file name for this episode.";
            }

            if (AnimeNamingFormatter.ExceedsNameLimit(name))
            {
                return $"'{name}' is longer than {AnimeNamingFormatter.MaxNameBytes} bytes; shorten the naming template.";
            }
        }

        return null;
    }

    // Sidecars are files named "<media base name>.<suffix>" (subtitles, NFO, ...) next to the media
    // file or in its Subs/Subtitles folder. Names that belong to a longer competing media base name
    // ("Show - 01.5.mkv" next to "Show - 01.mkv") stay with their own media file.
    private static IReadOnlyList<AnimeRenameMove> FindSidecars(string mediaPath, string targetDirectory, string targetBaseName)
    {
        var directory = Path.GetDirectoryName(mediaPath)!;
        var baseName = Path.GetFileNameWithoutExtension(mediaPath);
        var siblings = Directory.EnumerateFiles(directory).ToArray();
        var competing = siblings
            .Where(path => LibraryScanner.MediaExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name!.Length > baseName.Length && name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var sidecars = new List<AnimeRenameMove>();
        void Collect(IEnumerable<string> files, string destination)
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase) ||
                    LibraryScanner.MediaExtensions.Contains(Path.GetExtension(file)) ||
                    competing.Any(other => name.StartsWith(other + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                sidecars.Add(new AnimeRenameMove(
                    Path.GetFullPath(file),
                    Path.GetFullPath(Path.Combine(destination, targetBaseName + name[baseName.Length..]))));
            }
        }

        Collect(siblings, targetDirectory);
        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(subdirectory);
            if (SidecarDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Collect(Directory.EnumerateFiles(subdirectory), Path.Combine(targetDirectory, name));
            }
        }

        return sidecars;
    }

    // Season artwork (poster.jpg, fanart.jpg, ... in the season folder) belongs to that folder and
    // follows it when the profile renames the season folder. Artwork already in the target folder
    // stays and the source file is left where it is.
    private static IReadOnlyList<AnimeRenameMove> FindSeasonArtwork(
        string sourceDirectory,
        string targetDirectory,
        IReadOnlyList<AnimeRenameMove> folderMoves) =>
        Directory.EnumerateFiles(sourceDirectory)
            .Where(file => AnimeArtworkFiles.IsArtworkFileName(Path.GetFileName(file)))
            .Order(StringComparer.Ordinal)
            .Select(file => new AnimeRenameMove(
                Path.GetFullPath(file),
                Path.GetFullPath(Path.Combine(targetDirectory, Path.GetFileName(file)))))
            .Where(move => !File.Exists(Unrebase(move.TargetPath, folderMoves)))
            .ToArray();

    internal static AnimeNamingSeries BuildSeries(
        Anime anime,
        AnimeMetadata? metadata,
        string seriesFolder,
        AnimeSeriesType seriesType)
    {
        NfoProviderIds? nfoIds = null;
        int? nfoYear = null;
        var showNfo = Path.Combine(seriesFolder, NfoFileIndex.ShowFileName);
        if (File.Exists(showNfo) && NfoReader.ReadShow(showNfo).Value is { } show)
        {
            nfoIds = show.ProviderIds;
            nfoYear = show.Year;
        }

        var aniListId = metadata is not null &&
                        metadata.Provider.Equals(AniListMetadataProvider.ProviderKey, StringComparison.OrdinalIgnoreCase)
            ? metadata.ExternalId
            : nfoIds?.AniList;

        return new AnimeNamingSeries(
            anime.Title,
            metadata?.SeasonYear ?? nfoYear,
            seriesType,
            aniListId,
            nfoIds?.MyAnimeList,
            nfoIds?.Tvdb,
            nfoIds?.Tmdb,
            nfoIds?.Imdb);
    }

    // A multi-episode file (S01E01-E02 in the current name) keeps its episode range.
    private static IReadOnlyList<AnimeNamingEpisode> BuildNamingEpisodes(
        Episode episode,
        AnimeReleaseInfo? release,
        IReadOnlyDictionary<(int SeasonNumber, int Number), Episode> episodeByNumber,
        IReadOnlyDictionary<int, int> absoluteOffsets)
    {
        var last = episode.Number;
        if (release is { SeasonNumber: int season, EpisodeStart: int start, EpisodeEnd: int end } &&
            season == episode.SeasonNumber &&
            start == episode.Number &&
            end > start &&
            end - start < 50)
        {
            last = end;
        }

        var result = new List<AnimeNamingEpisode>();
        for (var number = episode.Number; number <= last; number++)
        {
            var title = number == episode.Number
                ? episode.Title
                : episodeByNumber.TryGetValue((episode.SeasonNumber, number), out var other) ? other.Title : "";
            int? absolute = absoluteOffsets.TryGetValue(episode.SeasonNumber, out var offset) ? offset + number : null;
            result.Add(new AnimeNamingEpisode(episode.SeasonNumber, number, absolute, title, release?.AirDate));
        }

        return result;
    }

    // Absolute numbers come from local numbering only: season N episode E is absolute
    // (episodes of seasons 1..N-1) + E when every earlier season is present as 1..count without
    // gaps. Specials and seasons after a gap have no absolute number, so the standard template applies.
    internal static Dictionary<int, int> LocalAbsoluteOffsets(IReadOnlyList<Episode> episodes)
    {
        var offsets = new Dictionary<int, int>();
        var offset = 0;
        foreach (var season in episodes
                     .Where(x => x.SeasonNumber > 0)
                     .GroupBy(x => x.SeasonNumber)
                     .OrderBy(group => group.Key))
        {
            if (season.Key != offsets.Count + 1)
            {
                break;
            }

            offsets[season.Key] = offset;
            var numbers = season.Select(x => x.Number).Distinct().Order().ToArray();
            if (numbers[0] != 1 || numbers[^1] != numbers.Length)
            {
                break;
            }

            offset += numbers.Length;
        }

        return offsets;
    }

    private sealed record Draft(
        MediaFile MediaFile,
        Episode Episode,
        string Source,
        string Target,
        string SourceSeriesFolder,
        IReadOnlyList<AnimeRenameMove> Sidecars,
        string? Reason)
    {
        public static Draft Blocked(MediaFile mediaFile, Episode episode, string source, string reason, string? target = null) =>
            new(mediaFile, episode, source, target ?? source, Path.GetDirectoryName(source)!, [], reason);

        public AnimeRenamePlanItem ToItem(AnimeRenameItemStatus status, string? reason) =>
            new(MediaFile.Id, Episode.Id, Episode.SeasonNumber, Episode.Number, Source, Target, status, reason, Sidecars, SourceSeriesFolder);
    }
}

public sealed class AnimeRenameException(string message, Exception innerException)
    : Exception(message, innerException);
