using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Naming;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Media.Optimization;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Import;

public sealed record AnimeImportActionResult(
    bool Success,
    string Message);

/// <summary>
/// The Anime importer behind the shared completed-download dispatcher
/// (<see cref="ICompletedDownloadImportAdapter"/>). The shared layer resolves the completed
/// download's location with the Anime remote path mappings, records the import on the download
/// Operation and finds and places the files (<see cref="CompletedDownloadFiles"/>,
/// <see cref="LibraryFilePlacer"/>); this class keeps only what is Anime-specific: it plans the
/// episode mapping with <see cref="AnimeImportPlanner"/>, consults Sonarr ownership before touching
/// any path, names the library path, preserves existing files until the replacement committed,
/// then reconciles that anime folder through the library scanner. Uncertain or failed files become
/// manual-intervention records the owner resolves on the acquisition overview.
/// </summary>
public sealed class AnimeImportExecutor(
    AppDbContext db,
    AnimeImportStore imports,
    SabnzbdAcquisitionStore acquisitions,
    AcquisitionOwnershipStore ownershipStore,
    SonarrObservationService observation,
    AnimeAcquisitionInventory inventory,
    AnimeNamingProfileStore namingStore,
    AnimeMonitoringStore monitoring,
    AnimeImportSettingsStore importSettings,
    IHardLinkCreator hardLinkCreator,
    AcquisitionHistoryService history,
    LibraryScanner scanner,
    ILogger<AnimeImportExecutor> logger,
    MediaOptimizationQueue? optimizationQueue = null,
    LibraryRootAvailabilityService? storage = null,
    IJularrEventPublisher? events = null,
    IInstanceModuleService? instanceModules = null) : ICompletedDownloadImportAdapter
{
    public const string OperationKind = "anime-import";
    public const string OperationCategory = "Library";
    public const string LogModule = "Import";
    public const string NoStoragePathMessage = "SABnzbd reported no storage path for the completed job.";
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromDays(7);

    // The SABnzbd monitor, startup recovery and owner actions run in different scopes; one
    // process-wide gate keeps two of them from executing the same import at the same time.
    private static readonly SemaphoreSlim ExecutionGate = new(1, 1);

    private readonly LibraryFilePlacer placer = new(new ImportFileTransfer(hardLinkCreator));

    // Held by a caller that swaps a library file (the lossless playback optimizer) so no import
    // moves files or rescans folders at the same moment; null while an import is executing.
    public static IDisposable? TryEnterExecution() =>
        ExecutionGate.Wait(0) ? new ExecutionLease() : null;

    public static bool IsAnimeDownload(OperationSnapshot operation) =>
        string.Equals(operation.Kind, SabnzbdAcquisitionService.OperationKind, StringComparison.Ordinal);

    public MediaAcquisitionKind Kind =>
        MediaAcquisitionKind.Anime;

    /// <summary>
    /// Imports the files of a completed anime SABnzbd job from the already mapped
    /// <see cref="CompletedDownloadImportRequest.SourcePath"/>. Idempotent per download operation:
    /// a finished record is reported as is, an interrupted one is resumed.
    /// </summary>
    public async Task<CompletedDownloadImportResult> ImportAsync(
        CompletedDownloadImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var download = request.Operation
            ?? throw new InvalidOperationException("An Anime import needs the download operation it answers.");

        await ExecutionGate.WaitAsync(cancellationToken);
        try
        {
            var record = await ImportCompletedCoreAsync(
                download,
                request.SourcePath,
                request,
                unavailableReason: null,
                cancellationToken);
            return record is null
                ? CompletedDownloadImportResult.Failed("The download is not an Anime acquisition download.")
                : await ToResultAsync(record, cancellationToken);
        }
        finally
        {
            ExecutionGate.Release();
        }
    }

    /// <summary>
    /// Ends the import of a completed download whose files can never be read (its download client
    /// no longer reports a path): records the failure so it shows on the acquisition overview.
    /// </summary>
    public async Task<AnimeImportRecord?> FailUnavailableAsync(
        OperationSnapshot download,
        string reason,
        CancellationToken cancellationToken)
    {
        await ExecutionGate.WaitAsync(cancellationToken);
        try
        {
            var record = await ImportCompletedCoreAsync(
                download,
                storagePath: null,
                request: null,
                reason,
                cancellationToken);
            if (record is not null)
            {
                await RecordOnDownloadAsync(record, cancellationToken);
            }

            return record;
        }
        finally
        {
            ExecutionGate.Release();
        }
    }

    /// <summary>Marks an import record failed, for a record whose download operation is gone.</summary>
    public async Task FailAsync(
        AnimeImportRecord record,
        string message,
        CancellationToken cancellationToken)
    {
        await ExecutionGate.WaitAsync(cancellationToken);
        try
        {
            var failed = await FinishAsync(record, AnimeImportStatus.Failed, message, record.ImportOperationId, cancellationToken);
            await RecordOnDownloadAsync(failed, cancellationToken);
        }
        finally
        {
            ExecutionGate.Release();
        }
    }

    private async Task<AnimeImportRecord?> ImportCompletedCoreAsync(
        OperationSnapshot download,
        string? storagePath,
        CompletedDownloadImportRequest? request,
        string? unavailableReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (!IsAnimeDownload(download))
        {
            return null;
        }

        var existing = await imports.FindByDownloadAsync(download.Id, cancellationToken);
        if (existing is not null && existing.Status != AnimeImportStatus.Importing)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var acquisition = (await acquisitions.FindByOperationAsync(download.Id, cancellationToken))?.Acquisition;
        var record = existing ?? new AnimeImportRecord(
            Guid.NewGuid(),
            download.Id,
            null,
            acquisition?.Id,
            acquisition?.AnimeKey ?? "",
            acquisition?.AnimeTitle ?? download.Subject ?? "Anime",
            storagePath ?? existing?.DownloadPath,
            AnimeImportStatus.Importing,
            [],
            null,
            now,
            now);

        if (acquisition is null)
        {
            return await FinishAsync(
                record with { AnimeKey = record.AnimeKey.Length == 0 ? "unknown" : record.AnimeKey },
                AnimeImportStatus.Failed,
                "No acquisition relation exists for this download, so its episodes are unknown. Import it manually.",
                null,
                cancellationToken);
        }

        var operations = new OperationStore(db);
        var operationId = record.ImportOperationId ?? await operations.CreateAsync(
            new OperationDescriptor(
                OperationKind,
                OperationCategory,
                "Anime import",
                $"{acquisition.AnimeTitle} · {SabnzbdAcquisitionService.FormatEpisodes(acquisition.Episodes)}",
                acquisition.ProfileId,
                OperationLane.Normal,
                Retryable: false),
            cancellationToken);
        if (record.ImportOperationId is null)
        {
            await operations.MarkRunningAsync(operationId, cancellationToken);
        }

        record = record with
        {
            ImportOperationId = operationId,
            DownloadPath = storagePath ?? record.DownloadPath,
            UpdatedAtUtc = now
        };
        await imports.UpsertAsync(record, cancellationToken);

        try
        {
            return await PlanAndExecuteAsync(
                record,
                acquisition,
                download,
                operationId,
                request,
                unavailableReason,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Anime import for download {OperationId} failed.", download.Id);
            return await FinishAsync(
                record,
                AnimeImportStatus.Failed,
                $"Import failed: {exception.Message}",
                operationId,
                cancellationToken);
        }
    }

    /// <summary>
    /// Owner decision for a file the planner did not import automatically: import it as the
    /// given local episode. Ownership rules still apply; only the confidence gate is bypassed.
    /// </summary>
    public async Task<AnimeImportActionResult> ImportManuallyAsync(
        Guid recordId,
        string sourcePath,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken)
    {
        await ExecutionGate.WaitAsync(cancellationToken);
        try
        {
            return await ImportManuallyCoreAsync(recordId, sourcePath, seasonNumber, episodeNumber, cancellationToken);
        }
        finally
        {
            ExecutionGate.Release();
        }
    }

    private async Task<AnimeImportActionResult> ImportManuallyCoreAsync(
        Guid recordId,
        string sourcePath,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken)
    {
        var record = await imports.GetAsync(recordId, cancellationToken);
        if (record is null)
        {
            return new(false, "Import record not found.");
        }

        var index = Array.FindIndex(record.Files, file =>
            file.SourcePath.Equals(sourcePath, StringComparison.Ordinal) &&
            file.Status is AnimeImportFileStatus.ManualRequired or AnimeImportFileStatus.Failed or AnimeImportFileStatus.Ignored);
        if (index < 0)
        {
            return new(false, "This file is not waiting for a manual import.");
        }

        if (seasonNumber < 0 || episodeNumber <= 0)
        {
            return new(false, "Choose a valid season and episode.");
        }

        var target = await inventory.LoadAsync(record.AnimeKey, cancellationToken);
        if (target is null)
        {
            return new(false, "The anime no longer exists in the library.");
        }

        if (!File.Exists(sourcePath))
        {
            return new(false, "The downloaded file no longer exists.");
        }

        if (await FindConflictingLibraryWorkAsync(null, cancellationToken) is { } waiting)
        {
            return new(false, waiting);
        }

        var snapshot = await observation.GetSnapshotAsync(forceRefresh: true, cancellationToken);
        var jobId = record.AcquisitionId?.ToString() ?? record.Id.ToString();
        var download = await new OperationStore(db).GetAsync(record.DownloadOperationId, cancellationToken);
        var allowed = SonarrParallelSafety.CanImport(
            snapshot,
            new AcquisitionImportRequest(record.AnimeKey, jobId, download?.ExternalId, sourcePath));
        if (!allowed.Allowed)
        {
            return new(false, $"Ownership: {allowed.Reason}");
        }

        var slot = target.Find(seasonNumber, episodeNumber);
        var requested = new RequestedAnimeEpisode(seasonNumber, episodeNumber, slot?.Key.AbsoluteEpisodeNumber);
        var replaced = new List<string>();
        if (slot?.FilePath is { } currentPath)
        {
            var mutation = SonarrParallelSafety.CanMutateLibraryPath(snapshot, record.AnimeKey, currentPath);
            if (!mutation.Allowed)
            {
                return new(false, $"The existing file for S{seasonNumber:00}E{episodeNumber:00} cannot be replaced. {mutation.Reason}");
            }

            replaced.Add(currentPath);
        }

        var monitoringState = await monitoring.LoadAsync(cancellationToken);
        var preferredRootId = monitoringState.Anime.TryGetValue(record.AnimeKey, out var monitorSettings)
            ? monitorSettings.TargetRootId
            : null;
        var location = await inventory.GetLibraryLocationAsync(target.Anime.Id, cancellationToken, preferredRootId);
        if (await WaitForLibraryStorageAsync(location, cancellationToken) is { } storageWaiting)
        {
            return new(false, storageWaiting);
        }

        var (importAction, allowHardlinkFallback) = ImportFileTransfer.Resolve(location!.Mode);

        var file = record.Files[index];
        var planned = new PlannedAnimeImport(
            new CompletedDownloadFile(file.SourcePath, file.SizeBytes),
            AnimeImportDisposition.AutoImport,
            importAction,
            [requested],
            file.SidecarPaths.Where(File.Exists).ToArray(),
            replaced,
            1,
            ["Imported manually by the owner."],
            allowHardlinkFallback);

        var operationId = record.ImportOperationId;
        var executed = await ExecuteFileAsync(planned, target, location, snapshot, jobId, operationId, cancellationToken);

        var files = record.Files.ToArray();
        files[index] = executed;
        var reconciled = executed.Status == AnimeImportFileStatus.Imported && location is not null
            ? await ReconcileAsync(location, operationId, cancellationToken)
            : null;

        var status = files.Any(item => item.Status is AnimeImportFileStatus.ManualRequired or AnimeImportFileStatus.Failed)
            ? AnimeImportStatus.ManualRequired
            : AnimeImportStatus.Imported;
        await QueuePlaybackOptimizationAsync([executed], record.AnimeTitle, download?.ProfileId, operationId, cancellationToken);
        var finished = await FinishAsync(
            record with { Files = files },
            status,
            executed.Status == AnimeImportFileStatus.Imported
                ? $"Imported {Path.GetFileName(executed.ImportedPath)} manually as S{seasonNumber:00}E{episodeNumber:00}.{reconciled}"
                : executed.Error ?? "Manual import did not complete.",
            operationId,
            cancellationToken);
        await RecordOnDownloadAsync(finished, cancellationToken);

        return executed.Status == AnimeImportFileStatus.Imported
            ? new(true, $"Imported as S{seasonNumber:00}E{episodeNumber:00}.")
            : new(false, executed.Error ?? "Manual import did not complete.");
    }

    public async Task<AnimeImportActionResult> DismissAsync(
        Guid recordId,
        CancellationToken cancellationToken)
    {
        await ExecutionGate.WaitAsync(cancellationToken);
        try
        {
            var record = await imports.GetAsync(recordId, cancellationToken);
            if (record is null)
            {
                return new(false, "Import record not found.");
            }

            if (!record.NeedsAttention)
            {
                return new(false, "Only imports waiting for attention can be dismissed.");
            }

            var dismissed = await FinishAsync(
                record,
                AnimeImportStatus.Dismissed,
                "Dismissed by the owner; the downloaded files were left untouched.",
                record.ImportOperationId,
                cancellationToken);
            await RecordOnDownloadAsync(dismissed, cancellationToken);
            return new(true, "Import dismissed.");
        }
        finally
        {
            ExecutionGate.Release();
        }
    }

    private async Task<AnimeImportRecord> PlanAndExecuteAsync(
        AnimeImportRecord record,
        SabnzbdAcquisition acquisition,
        OperationSnapshot download,
        Guid operationId,
        CompletedDownloadImportRequest? request,
        string? unavailableReason,
        CancellationToken cancellationToken)
    {
        if (await FindConflictingLibraryWorkAsync(operationId, cancellationToken) is { } waiting)
        {
            // Stays Importing; the scheduler resumes it on its next run.
            var deferred = record with { Message = waiting, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await imports.UpsertAsync(deferred, cancellationToken);
            await new OperationStore(db).ReportProgressAsync(operationId, null, waiting, cancellationToken: cancellationToken);
            return deferred;
        }

        var target = await inventory.LoadAsync(acquisition.AnimeKey, cancellationToken);
        if (target is null)
        {
            return await FinishAsync(record, AnimeImportStatus.Failed, "The anime no longer exists in the library.", operationId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(record.DownloadPath))
        {
            return await FinishAsync(record, AnimeImportStatus.Failed, unavailableReason ?? NoStoragePathMessage, operationId, cancellationToken);
        }

        var files = CompletedDownloadFiles.Enumerate(record.DownloadPath, out var enumerationError);
        if (enumerationError is not null)
        {
            return await FinishAsync(record, AnimeImportStatus.Failed, enumerationError, operationId, cancellationToken);
        }

        var requested = acquisition.Episodes
            .Select(key => new RequestedAnimeEpisode(
                key.SeasonNumber,
                key.EpisodeNumber,
                key.AbsoluteEpisodeNumber ?? target.Find(key.SeasonNumber, key.EpisodeNumber)?.Key.AbsoluteEpisodeNumber))
            .ToArray();
        if (requested.Length == 0)
        {
            return await FinishAsync(record, AnimeImportStatus.Failed, "The acquisition names no episodes.", operationId, cancellationToken);
        }

        var aliases = requested
            .SelectMany(episode => target.Find(episode.SeasonNumber, episode.EpisodeNumber) is { } slot
                ? new[] { slot.SearchTitle }.Concat(slot.SearchAliases)
                : [])
            .Append(target.Anime.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var existing = target.Episodes
            .Where(episode => episode.HasFile)
            .Select(episode => new ExistingAnimeFile(
                episode.FilePath!,
                episode.Key.SeasonNumber,
                episode.Key.EpisodeNumber,
                episode.FileSizeBytes ?? 0))
            .ToArray();

        var snapshot = await observation.GetSnapshotAsync(forceRefresh: true, cancellationToken);
        var jobId = acquisition.Id.ToString();
        var monitoringState = await monitoring.LoadAsync(cancellationToken);
        var preferredRootId = monitoringState.Anime.TryGetValue(acquisition.AnimeKey, out var monitorSettings)
            ? monitorSettings.TargetRootId
            : null;
        var location = await inventory.GetLibraryLocationAsync(target.Anime.Id, cancellationToken, preferredRootId);
        if (await WaitForLibraryStorageAsync(location, cancellationToken) is { } storageWaiting)
        {
            // Nothing was moved yet; stays Importing and the scheduler resumes it on its next run.
            var deferred = record with { Message = storageWaiting, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await imports.UpsertAsync(deferred, cancellationToken);
            await new OperationStore(db).ReportProgressAsync(operationId, null, storageWaiting, cancellationToken: cancellationToken);
            return deferred;
        }

        var (importAction, allowHardlinkFallback) = ImportFileTransfer.Resolve(location!.Mode);
        var plan = AnimeImportPlanner.Plan(
            new AnimeImportPlanContext(
                jobId,
                acquisition.AnimeKey,
                aliases,
                requested,
                target.Profile,
                importAction,
                allowHardlinkFallback,
                DownloadId: download.ExternalId),
            files,
            existing,
            snapshot);

        var operations = new OperationStore(db);
        if (plan.BlockedByOwnership)
        {
            await operations.AppendLogAsync(operationId, OperationLogLevel.Warning, LogModule, plan.OwnershipBlockReason!, cancellationToken);
            return await FinishAsync(
                record with { Files = plan.Files.Select(file => ToRecord(file, AnimeImportFileStatus.Ignored, null, null)).ToArray() },
                AnimeImportStatus.Failed,
                plan.OwnershipBlockReason!,
                operationId,
                cancellationToken);
        }

        if (plan.Files.Count == 0)
        {
            return await FinishAsync(record, AnimeImportStatus.Failed, "The completed download contains no files.", operationId, cancellationToken);
        }

        await ownershipStore.UpdateAsync(
            state => state.Jobs.TryGetValue(jobId, out var job)
                ? SonarrParallelSafety.RegisterJob(state, job with { Status = AcquisitionOwnershipStatus.Importing, UpdatedAtUtc = DateTimeOffset.UtcNow })
                : state,
            cancellationToken);

        var results = new List<AnimeImportFileRecord>();
        foreach (var planned in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(planned.Source.Path);
            switch (planned.Disposition)
            {
                case AnimeImportDisposition.AutoImport:
                    results.Add(await ExecuteFileAsync(planned, target, location, snapshot, jobId, operationId, cancellationToken));
                    break;

                case AnimeImportDisposition.ManualReview:
                    await operations.AppendLogAsync(operationId, OperationLogLevel.Warning, LogModule, $"Manual review: {name} — {string.Join(" ", planned.Reasons)}", cancellationToken);
                    results.Add(ToRecord(planned, AnimeImportFileStatus.ManualRequired, null, null));
                    break;

                default:
                    if (planned.Targets.Count > 0 || !CompletedDownloadFiles.IsSidecarOrJunk(planned.Source.Path))
                    {
                        await operations.AppendLogAsync(operationId, OperationLogLevel.Information, LogModule, $"Ignored: {name} — {string.Join(" ", planned.Reasons)}", cancellationToken);
                    }

                    results.Add(ToRecord(planned, AnimeImportFileStatus.Ignored, null, null));
                    break;
            }
        }

        var imported = results.Count(result => result.Status == AnimeImportFileStatus.Imported);
        string? reconciled = null;
        if (imported > 0 && location is not null)
        {
            if (request is not null)
            {
                await request.ReportProgressAsync(
                    CompletedDownloadImportPhase.MatchingMetadata,
                    "Matching imported Anime metadata.");
            }

            reconciled = await ReconcileAsync(location, operationId, cancellationToken);
        }

        var attention = results.Any(result => result.Status is AnimeImportFileStatus.ManualRequired or AnimeImportFileStatus.Failed);
        var status = attention
            ? AnimeImportStatus.ManualRequired
            : imported > 0
                ? AnimeImportStatus.Imported
                : AnimeImportStatus.Failed;
        var message = status switch
        {
            AnimeImportStatus.Imported => $"Imported {imported} file(s) into the library.{reconciled}",
            AnimeImportStatus.ManualRequired => imported > 0
                ? $"Imported {imported} file(s); {results.Count - imported} need(s) a manual decision.{reconciled}"
                : "The download needs a manual import decision.",
            _ => "No video file could be imported."
        };

        await QueuePlaybackOptimizationAsync(results, acquisition.AnimeTitle, acquisition.ProfileId, operationId, cancellationToken);
        return await FinishAsync(
            record with { Files = results.ToArray() },
            status,
            message,
            operationId,
            cancellationToken);
    }

    private async Task<AnimeImportFileRecord> ExecuteFileAsync(
        PlannedAnimeImport planned,
        AnimeAcquisitionTarget target,
        AnimeLibraryLocation? location,
        AcquisitionOwnershipSnapshot snapshot,
        string jobId,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        var operations = new OperationStore(db);
        var name = Path.GetFileName(planned.Source.Path);

        async Task<AnimeImportFileRecord> ManualAsync(string reason)
        {
            if (operationId is { } id)
            {
                await operations.AppendLogAsync(id, OperationLogLevel.Warning, LogModule, $"Manual review: {name} — {reason}", cancellationToken);
            }

            return ToRecord(planned, AnimeImportFileStatus.ManualRequired, null, reason);
        }

        if (LibraryFilePlacer.FindSourceProblem(planned.Source.Path) is { } sourceProblem)
        {
            return await ManualAsync(sourceProblem);
        }

        var named = await BuildTargetAsync(target, location, planned.Targets, planned.Source.Path, cancellationToken);
        if (named is null)
        {
            return await ManualAsync("The anime no longer exists in the library.");
        }

        if (named.Problem is not null)
        {
            return await ManualAsync(named.Problem);
        }

        var destination = named.Path;

        if (LibraryFilePlacer.FindDestinationConflict(destination, planned.ExistingPathsToReplaceAfterCommit) is { } conflict)
        {
            return await ManualAsync(conflict);
        }

        // Claim the destination for this job first so parallel mode can mutate it, then verify
        // Sonarr does not act on it. The claim is withdrawn when the check fails.
        var state = await ownershipStore.UpdateAsync(
            current => current.Paths.ContainsKey(SonarrParallelSafety.NormalizePath(destination))
                ? current
                : SonarrParallelSafety.RegisterPath(
                    current,
                    new ManagedMediaPath(destination, target.Anime.Key, AcquisitionOwner.Jularr, jobId, DateTimeOffset.UtcNow)),
            cancellationToken);
        var mutation = SonarrParallelSafety.CanMutateLibraryPath(snapshot with { State = state }, target.Anime.Key, destination);
        if (!mutation.Allowed)
        {
            await ReleasePathAsync(destination, jobId, cancellationToken);
            return await ManualAsync($"Ownership: {mutation.Reason}");
        }

        // The shared commit step: transfer with the owner's import mode, follow with the sidecars
        // and delete the replaced files only after the new file is in place.
        IReadOnlyList<string> notes;
        try
        {
            notes = placer.Place(new LibraryFilePlacement(
                planned.Source.Path,
                destination,
                planned.FileAction,
                planned.AllowHardlinkFallbackToCopy,
                planned.SidecarPaths
                    .Select(sidecar => new PlacedSidecar(
                        sidecar,
                        AnimeImportDestination.BuildSidecarName(name, Path.GetFileName(sidecar), Path.GetFileName(destination))))
                    .ToArray(),
                planned.ExistingPathsToReplaceAfterCommit));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ReleasePathAsync(destination, jobId, cancellationToken);
            var error = $"The library folder is not writable or the file could not be moved: {exception.Message}";
            if (operationId is { } failedId)
            {
                await operations.AppendLogAsync(failedId, OperationLogLevel.Error, LogModule, $"Failed: {name} — {error}", cancellationToken);
            }

            return ToRecord(planned, AnimeImportFileStatus.Failed, null, error);
        }

        if (operationId is { } importedId)
        {
            var targets = string.Join(", ", planned.Targets.Select(item => $"S{item.SeasonNumber:00}E{item.EpisodeNumber:00}"));
            await operations.AppendLogAsync(
                importedId,
                OperationLogLevel.Information,
                LogModule,
                $"Imported {name} as {targets} → {Path.GetFileName(destination)} (confidence {planned.Confidence:0.00}). {string.Join(" ", planned.Reasons.Concat(notes))}",
                cancellationToken);
        }

        var isUpgrade = planned.ExistingPathsToReplaceAfterCommit.Count > 0;
        var release = AnimeReleaseParser.Parse(name);
        var score = AnimeReleaseScorer.Score(target.Profile, new AnimeReleaseCandidate(release, planned.Source.SizeBytes));
        foreach (var episodeTarget in planned.Targets)
        {
            await history.RecordAsync(
                new AcquisitionHistoryEntry
                {
                    AnimeId = target.Anime.Id,
                    SeasonNumber = episodeTarget.SeasonNumber,
                    EpisodeNumber = episodeTarget.EpisodeNumber,
                    AbsoluteEpisodeNumber = episodeTarget.AbsoluteEpisodeNumber,
                    EventKind = isUpgrade ? AcquisitionHistoryEventKind.Upgraded : AcquisitionHistoryEventKind.Imported,
                    ReleaseTitle = name,
                    ReleaseKey = release.ReleaseKey,
                    Score = score.Score,
                    QualityKey = score.QualityKey,
                    Reason = string.Join(" ", planned.Reasons),
                    OccurredAtUtc = DateTime.UtcNow
                },
                cancellationToken);
        }

        return ToRecord(planned, AnimeImportFileStatus.Imported, destination, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private async Task<AnimeImportTarget?> BuildTargetAsync(
        AnimeAcquisitionTarget target,
        AnimeLibraryLocation location,
        IReadOnlyList<RequestedAnimeEpisode> targets,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var anime = await db.Anime.AsNoTracking().SingleOrDefaultAsync(item => item.Id == target.Anime.Id, cancellationToken);
        if (anime is null)
        {
            return null;
        }

        var metadata = await db.AnimeMetadata.AsNoTracking().SingleOrDefaultAsync(item => item.AnimeId == anime.Id, cancellationToken);
        var episodes = await db.Episodes.AsNoTracking().Where(item => item.AnimeId == anime.Id).ToListAsync(cancellationToken);
        var naming = await namingStore.LoadAsync(cancellationToken);
        return AnimeImportDestination.Build(naming, anime, metadata, episodes, location, targets, sourcePath);
    }

    // Library scans and renames enumerate or move the same folders; an import waits for them
    // (AnimeRenameService refuses to start while an import runs, for the same reason).
    private async Task<string?> FindConflictingLibraryWorkAsync(
        Guid? ownOperationId,
        CancellationToken cancellationToken)
    {
        var active = await new OperationStore(db).ListAsync(
            new OperationListFilter(View: "active", Category: OperationCategory),
            cancellationToken);
        return active
            .Where(operation =>
                operation.Id != ownOperationId &&
                AnimeRenameService.ConflictingOperationKinds.Contains(operation.Kind, StringComparer.Ordinal) &&
                operation.Kind != OperationKind)
            .Select(operation => $"Waiting for '{operation.Title}' to finish before importing.")
            .FirstOrDefault();
    }

    // An import needs a destination and its library storage now. With no LibraryRoot to import into the import waits for the owner to
    // choose one in Admin → Storage; a sleeping Wake-on-LAN NAS is started and the import waits for the bounded start attempt. Returns
    // the reason to defer, so no file is moved without a destination or towards an offline mount.
    private async Task<string?> WaitForLibraryStorageAsync(
        AnimeLibraryLocation? location,
        CancellationToken cancellationToken)
    {
        if (location is null)
        {
            return LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Anime);
        }

        if (storage is null)
        {
            return null;
        }

        var status = await storage.RequireAsync(location.RootId, waitForStart: true, cancellationToken);
        return status is null or { IsAvailable: true }
            ? null
            : "Waiting for the library's media storage to come online before importing.";
    }

    private async Task<string> ReconcileAsync(
        AnimeLibraryLocation location,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = location.AnimeDirectory is null
                ? await scanner.ScanAsync(location.RootId, cancellationToken)
                : await scanner.ScanFolderAsync(
                    location.RootId,
                    Path.GetRelativePath(location.RootPath, location.AnimeDirectory),
                    progress: null,
                    cancellationToken);
            var summary = $" Library reconciled: {result.Discovered} added, {result.Updated} changed, {result.Removed} removed.";
            if (operationId is { } id)
            {
                await new OperationStore(db).AppendLogAsync(id, OperationLogLevel.Information, LogModule, summary.Trim(), cancellationToken);
            }

            return summary;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Library reconciliation after an anime import failed for {Directory}.", location.AnimeDirectory);
            if (operationId is { } id)
            {
                await new OperationStore(db).AppendLogAsync(id, OperationLogLevel.Warning, LogModule, $"Library reconciliation failed; rescan the root manually. {exception.Message}", cancellationToken);
            }

            return " Library reconciliation failed; rescan the root manually.";
        }
    }

    // Post-import step: queues the lossless playback optimization of the files this run imported as
    // its own Operation, so a remux never holds the import open. The optimizer re-checks ownership
    // and waits for this import to finish before it replaces anything.
    private async Task QueuePlaybackOptimizationAsync(
        IEnumerable<AnimeImportFileRecord> files,
        string subject,
        string? profileId,
        Guid? importOperationId,
        CancellationToken cancellationToken)
    {
        // Remuxing for Direct Play spawns ffmpeg and only serves the player: an instance without Playback never starts it.
        if (optimizationQueue is null || (instanceModules is not null && !await instanceModules.IsEnabledAsync(InstanceModule.Playback, cancellationToken)))
        {
            return;
        }

        var paths = files
            .Where(file => file.Status == AnimeImportFileStatus.Imported && file.ImportedPath is not null)
            .Select(file => Path.GetFullPath(file.ImportedPath!))
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var settings = await importSettings.LoadAsync(cancellationToken);
        if (settings.PlaybackOptimization == LosslessPlaybackOptimizationMode.Off)
        {
            return;
        }

        var mediaFileIds = await db.MediaFiles
            .AsNoTracking()
            .Where(media => paths.Contains(media.Path))
            .Select(media => media.Id)
            .ToArrayAsync(cancellationToken);
        if (mediaFileIds.Length == 0)
        {
            return;
        }

        string message;
        try
        {
            // The background queue is bounded; never let a full queue hold the import gate.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await optimizationQueue.QueueAsync(mediaFileIds, subject, profileId, timeout.Token);
            message = $"Queued lossless playback optimization for {mediaFileIds.Length} file(s).";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            message = "Lossless playback optimization was not queued because the background queue is full; run it from the anime's repair page.";
        }

        if (importOperationId is { } id)
        {
            await new OperationStore(db).AppendLogAsync(id, OperationLogLevel.Information, LogModule, message, cancellationToken);
        }
    }

    private async Task<AnimeImportRecord> FinishAsync(
        AnimeImportRecord record,
        AnimeImportStatus status,
        string message,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        var finished = record with
        {
            Status = status,
            Message = message,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await imports.UpsertAsync(finished, cancellationToken);

        if (record.AcquisitionId is { } acquisitionId)
        {
            var jobStatus = status switch
            {
                AnimeImportStatus.Imported => AcquisitionOwnershipStatus.Completed,
                AnimeImportStatus.Failed => AcquisitionOwnershipStatus.Failed,
                AnimeImportStatus.Dismissed => AcquisitionOwnershipStatus.Cancelled,
                _ => AcquisitionOwnershipStatus.Importing
            };
            var jobId = acquisitionId.ToString();
            await ownershipStore.UpdateAsync(
                state => state.Jobs.TryGetValue(jobId, out var job) && job.Status != jobStatus
                    ? SonarrParallelSafety.RegisterJob(state, job with { Status = jobStatus, UpdatedAtUtc = DateTimeOffset.UtcNow })
                    : state,
                cancellationToken);
        }

        // A manual-import state finishes the operation too: the import record carries the pending
        // decision, and a running operation would block renames of the anime indefinitely.
        if (operationId is { } id)
        {
            // The one Mark*Async call for the "anime-import" Kind: passing events here is what
            // makes OperationStore publish #429's ImportCompleted/ImportFailed for anime (#579).
            var operations = new OperationStore(db, events);
            if (status == AnimeImportStatus.Failed)
            {
                await operations.MarkFailedAsync(id, message, CancellationToken.None);
            }
            else
            {
                await operations.MarkSucceededAsync(id, message, CancellationToken.None);
            }
        }

        return finished;
    }

    /// <summary>
    /// The importer result of an import record for the shared layer: the state it stands for and
    /// where the files went (the imported files' folder) with which import mode.
    /// </summary>
    private async Task<CompletedDownloadImportResult> ToResultAsync(
        AnimeImportRecord record,
        CancellationToken cancellationToken)
    {
        var imported = record.Files
            .Select(file => file.ImportedPath)
            .OfType<string>()
            .ToArray();
        CompletedDownloadPlacement? placement = null;
        if (imported.Length > 0 && Path.GetDirectoryName(imported[0]) is { } destination)
        {
            var full = Path.GetFullPath(destination);
            var roots = await db.LibraryRoots.AsNoTracking().ToListAsync(cancellationToken);
            var root = roots
                .Where(candidate => full.StartsWith(Path.GetFullPath(candidate.Path), StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Path.Length)
                .FirstOrDefault();
            placement = new CompletedDownloadPlacement(
                destination,
                ImportFileTransfer.ModeFor(root?.PlacementPolicy ?? LibraryPlacementPolicy.HardlinkOrCopy));
        }

        var message = string.IsNullOrWhiteSpace(record.Message) ? "Anime import finished." : record.Message;
        return record.Status switch
        {
            AnimeImportStatus.Imported => CompletedDownloadImportResult.Completed(message, resultUrl: null, placement),
            AnimeImportStatus.ManualRequired => CompletedDownloadImportResult.NeedsReview(message, placement),
            // Waiting for a library scan, rename or offline storage: the scheduler resumes it.
            AnimeImportStatus.Importing => CompletedDownloadImportResult.RetryLater(message, placement),
            _ => CompletedDownloadImportResult.Failed(message, placement)
        };
    }

    /// <summary>
    /// Shows an owner decision on an anime import (manual import, dismiss) on its download
    /// Operation like the shared import service shows the automatic import.
    /// </summary>
    private async Task RecordOnDownloadAsync(AnimeImportRecord record, CancellationToken cancellationToken)
    {
        var result = await ToResultAsync(record, cancellationToken);
        await DownloadImportRecorder.RecordResultAsync(
            new OperationStore(db),
            record.DownloadOperationId,
            result,
            location: null,
            DateTime.UtcNow,
            CancellationToken.None);
    }
    private async Task ReleasePathAsync(string path, string jobId, CancellationToken cancellationToken)
    {
        var normalized = SonarrParallelSafety.NormalizePath(path);
        await ownershipStore.UpdateAsync(
            state =>
            {
                if (!state.Paths.TryGetValue(normalized, out var owned) || owned.JobId != jobId)
                {
                    return state;
                }

                var paths = new Dictionary<string, ManagedMediaPath>(state.Paths, StringComparer.OrdinalIgnoreCase);
                paths.Remove(normalized);
                return state with { Paths = paths };
            },
            cancellationToken);
    }

    private sealed class ExecutionLease : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                ExecutionGate.Release();
            }
        }
    }

    private static AnimeImportFileRecord ToRecord(
        PlannedAnimeImport planned,
        AnimeImportFileStatus status,
        string? importedPath,
        string? error) =>
        new(
            planned.Source.Path,
            planned.Source.SizeBytes,
            status,
            planned.Targets.ToArray(),
            planned.SidecarPaths.ToArray(),
            planned.ExistingPathsToReplaceAfterCommit.ToArray(),
            planned.Confidence,
            planned.Reasons.ToArray(),
            importedPath,
            error);
}
