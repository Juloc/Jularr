using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Integrations.Logic.Qbittorrent;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // Shared by every downloader: plan, check, move and assign; waits and failures are recorded the same way.
    internal sealed class DownloadImport(PlexServer plex, LibraryReplacements replacements, StorageAccess storage, TimeProvider time, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadStorage downloads,
        IDataProtectionProvider protection, WorkSignals signals)
    {
        public const string UnmatchedFilesKey = "import_unmatched_files";
        public const string NothingPlaceableKey = "import_nothing_placeable";

        public async Task PlaceAsync(LogicContext context, DownloadData download, StorageFiles source, ImportSourceRef sourceRef, IReadOnlyList<StorageFile> files, bool isComplete, CancellationToken ct,
            IReadOnlyList<StorageFile>? allSourceFiles = null)
        {
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SetDownloadStageAsync(new DownloadOperationParameters(download.OperationId), DownloadStateType.Completed, token);
                return true;
            }, ct);
            var sourceDirectory = sourceRef.Directory;
            if (!isComplete)
            {
                await AttentionAsync(context, download.OperationId, "import_source_unavailable", null, ct);
                return;
            }

            var (root, patterns, target, units, chapterUnits) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadImportRootAsync(download.WantedItemId, token), await ctx.ReadNamingPatternsAsync(token), await ctx.ReadWantedTargetAsync(download.WantedItemId, token),
                    download.MediaTypeId == MediaType.Series ? await ctx.ReadUnitStatesAsync(download.WantedItemId, token)
                    : download.MediaSubtypeId is MediaSubtype.LightNovel or MediaSubtype.Manga ? await ctx.ReadBookUnitStatesAsync(download.WantedItemId, WorkUnitType.Volume, token) : [],
                    download.MediaSubtypeId == MediaSubtype.Manga ? await ctx.ReadBookUnitStatesAsync(download.WantedItemId, WorkUnitType.Chapter, token) : []), ct);
            if (root is null)
            {
                await AttentionAsync(context, download.OperationId, "import_no_library", null, ct);
                return;
            }

            // Episodes of a series, volumes of a light novel or manga, a manga's chapters with their volume.
            IReadOnlyList<TargetUnit> known = download.MediaTypeId == MediaType.Series ? EpisodeNumbering.Episodes(units)
                : [.. units.Select(unit => new TargetUnit(unit.WorkUnitId, null, unit.Number, unit.HasMedia, title: unit.Title))];
            IReadOnlyList<TargetUnit> chapters = [.. chapterUnits.Select(unit => new TargetUnit(unit.WorkUnitId, unit.ParentNumber, unit.Number, unit.HasMedia, title: unit.Title))];

            var facts = ReleaseFacts.OfRelease(download.Title, download.MediaTypeId == MediaType.Movie, download.QualityKey);

            // A clear film or episode imports by itself, one of no use is excluded and searched again at once, a doubt waits for a person with its reason.
            // A download with nothing a person could assign (an empty folder, or only archives and release files) is of no use either.
            var verdict = files.Count == 0 ? new ImportVerdict(ImportVerdictKind.Discard, ImportJudgement.NoFile)
                : ImportJudgement.Judge(download.MediaTypeId, download.MediaSubtypeId, [download.CanonicalTitle, .. target.Titles], download.ReleaseYear, download.Title, download.ExpectedParentUnitNumber,
                    download.ExpectedUnitStart, download.ExpectedUnitEnd, files, known, target.Definition is null ? null : AcquisitionSelectionLogic.ReadProfile(target));
            if (verdict.Kind == ImportVerdictKind.Discard)
            {
                // The folder says it is empty only when nothing is in it but release files.
                var reasonKey = verdict.ReasonKey!;
                if (files.Count == 0)
                {
                    var everyFile = allSourceFiles ?? await ListAsync(source, sourceDirectory, files, ct);
                    if (ImportPlacementLogic.Unplaced(everyFile, files, []).Count == 0) reasonKey = ImportJudgement.EmptyFolder;
                }

                await FailAsync(context, download.OperationId, reasonKey, time.GetUtcNow().UtcDateTime, ct);
                return;
            }

            if (verdict.Kind == ImportVerdictKind.Doubt)
            {
                await AttentionAsync(context, download.OperationId, verdict.ReasonKey!, null, ct, ImportProposal.Write(sourceRef, verdict.Proposal));
                return;
            }

            // Music follows its tags; ebooks and comics must be what their names say, and one that is not is left out with the check it failed.
            Dictionary<string, AudioTags>? tags = download.MediaTypeId == MediaType.Music ? new(StringComparer.Ordinal) : null;
            var unplaced = new List<UnplacedFile>();
            IReadOnlyList<ImportPlacementData> placements;
            try
            {
                if (tags is not null)
                {
                    foreach (var file in files.Where(file => LibraryFileTypes.GetAssetType(file.RelativePath) == MediaAssetType.Audio))
                    {
                        await using var audio = await source.OpenReadAsync(StoragePath.Combine(sourceDirectory, file.RelativePath), ct);
                        if (AudioTagReader.Read(StoragePath.Name(file.RelativePath), audio) is { } trackTags) tags[file.RelativePath] = trackTags;
                    }
                }

                var candidates = new List<StorageFile>();
                foreach (var file in files)
                {
                    if (download.MediaTypeId == MediaType.Book && LibraryFileTypes.GetAssetType(file.RelativePath) is MediaAssetType.Ebook or MediaAssetType.Comic)
                    {
                        await using var document = await source.OpenReadAsync(StoragePath.Combine(sourceDirectory, file.RelativePath), ct);
                        if (await LibraryFileTypes.CheckDocumentAsync(LibraryFileTypes.Extension(file.RelativePath), document, ct) is { } failed)
                        {
                            unplaced.Add(new UnplacedFile(file.RelativePath, failed));
                            continue;
                        }
                    }

                    candidates.Add(file);
                }

                placements = ImportPlacementLogic.Plan(download.MediaTypeId, download.MediaSubtypeId, download.CanonicalTitle, download.ReleaseYear, download.QualityKey,
                    candidates, patterns, facts, download.Author, download.TrackCount, download.PlatformName, known, tags, ImportPlacementLogic.KnownValues(target), unplaced, chapters);
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                await AttentionAsync(context, download.OperationId, "import_source_unavailable", null, ct);
                return;
            }

            // Files of a book that name no unit of the Work wait for a person, with what else was left out and why.
            var unmatched = unplaced.Any(file => file.NamesNoUnit);
            IReadOnlyList<ImportFileProposal> leftOut = [.. unplaced.Where(file => file.Reason != UnplacedFile.Present).OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new ImportFileProposal(file.Path, null, null, null, null, file.Reason))];

            // What a person need not import by hand: what this import places and what the library already holds.
            string[] settled = [.. placements.Select(placement => placement.SourcePath), .. unplaced.Where(file => file.Reason == UnplacedFile.Present).Select(file => file.Path)];
            if (placements.Count == 0)
            {
                // A pack whose volumes the library already holds is done, or waits with its files of no volume.
                if (unplaced.Any(file => file.Reason == UnplacedFile.Present) && unplaced.All(file => file.Reason == UnplacedFile.Present || file.NamesNoUnit))
                {
                    if (unmatched)
                    {
                        await AttentionAsync(context, download.OperationId, UnmatchedFilesKey, null, ct, ImportProposal.Write(sourceRef, leftOut, settled));
                        return;
                    }

                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.CompleteDownloadAsync(new DownloadOperationParameters(download.OperationId), token);
                        return true;
                    }, ct);
                    return;
                }

                // Nothing could be placed: every file stays for a person with why, where a failed download would be cleaned up.
                var everyFile = allSourceFiles ?? await ListAsync(source, sourceDirectory, files, ct);
                await AttentionAsync(context, download.OperationId, NothingPlaceableKey, null, ct, ImportProposal.Write(sourceRef, ImportPlacementLogic.Unplaced(everyFile, files, unplaced)));
                return;
            }

            var acquisitionProfile = target.Definition is null ? null : AcquisitionSelectionLogic.ReadProfile(target);

            // A trustworthy EPUB language tag can contradict a strictly configured source
            // release. Hold the original file for the Owner instead of importing the wrong language.
            // EPUBs without verifiable language metadata wait for manual review under strict profiles.
            // PDFs have no reliable language reader here; selection still requires release language evidence.
            if (acquisitionProfile is { IsBook: true, LanguageFallback: LanguageFallbackType.None, LanguageOrder.Length: > 0 })
            {
                try
                {
                    foreach (var placement in placements.Where(placement => placement.Extension.Equals(".epub", StringComparison.OrdinalIgnoreCase)))
                    {
                        await using var book = await source.OpenReadAsync(StoragePath.Combine(sourceDirectory, placement.SourcePath), ct);
                        var languageIssue = BookDocumentLanguages.StrictEpubAttentionReason(acquisitionProfile, BookDocumentLanguages.ReadEpub(book));
                        if (languageIssue is not null)
                        {
                            // The book keeps where it is, so a person who knows its language can import it by hand.
                            await AttentionAsync(context, download.OperationId, languageIssue, null, ct, ImportProposal.Write(sourceRef, []));
                            return;
                        }
                    }
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                {
                    await AttentionAsync(context, download.OperationId, "import_source_unavailable", null, ct);
                    return;
                }
            }

            var commit = await CommitAsync(context, new ImportCommitRequest(root, download.WorkId, download.GamePlatformId, placements, source, sourceDirectory, files,
                path => download.MediaTypeId == MediaType.Game ? new ReleaseFact(TextLimit.Cut(Path.GetFileName(path), 500)!, facts.ReleaseGroup, facts.QualityKey, facts.Edition, facts.Revision) : facts,
                download.OperationId, null, download.MediaTypeId, acquisitionProfile, FinishesDownload: !unmatched), ct);
            // A storage that comes back heals by itself on the next pass; a name conflict or a refused assignment waits for a person, who can import the files by hand from the queue.
            if (commit.FailureKey is { } failure)
            {
                await AttentionAsync(context, download.OperationId, failure, null, ct, failure is "import_target_conflict" or "import_assignment_failed" ? ImportProposal.Write(sourceRef, []) : null);
                return;
            }

            // What matched is in the library; the download keeps waiting with the files that name no unit.
            if (unmatched)
            {
                await AttentionAsync(context, download.OperationId, UnmatchedFilesKey, null, ct, ImportProposal.Write(sourceRef, leftOut, settled));
                return;
            }

            // The client lets go of the job once the move left nothing an import would take.
            await RemoveFromClientAsync(context, download.AcquisitionConnectionId, download.ExternalId, source, sourceDirectory, allSourceFiles is not null, ct);
        }

        // Moves the planned files into the library and assigns them to their work; the failure key names what a person must settle, and nothing is left half done.
        // A file is first linked into place on the same filesystem (else copied and checked), and its source goes only once the library holds it: a failure leaves the source as it was.
        public async Task<ImportCommit> CommitAsync(LogicContext context, ImportCommitRequest request, CancellationToken ct)
        {
            // Place outside transactions; an existing library file is never taken over.
            // An offline library keeps the download waiting instead of writing elsewhere.
            var (root, placements, source, sourceDirectory, files) = (request.Root, request.Placements, request.Source, request.SourceDirectory, request.Files);
            var directory = root.RelativePath;
            StorageFiles library;
            string libraryName;
            try
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(root.StorageMountId, token), ct);
                libraryName = mount.Name;
                library = storage.Open(mount);
                if (await library.GetAsync(directory, ct) is not { IsDirectory: true })
                {
                    throw new NasMountException("storage_unavailable");
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                return new ImportCommit("import_library_unavailable", []);
            }

            // A name the inventory still gives an assigned file is taken, also when that file went missing: the new file could never be assigned under it.
            var folders = JsonSerializer.Serialize(placements.Select(placement => StoragePath.Parent(placement.TargetStem)).Distinct(StringComparer.Ordinal).ToArray());
            var assigned = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAssignedStoredPathsAsync(new StoredFolderData(root.Id, folders), token), ct);

            var imported = new List<ImportedFile>();
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            var media = new List<(string RelativePath, ImportPlacementData Placement)>();
            try
            {
                var canLink = false;
                long? freeBytes;
                if (library is LocalStorageFiles local)
                {
                    var targetDirectory = local.ResolveDirectory(directory);
                    freeBytes = StorageFileSystem.AvailableFreeSpace(targetDirectory);
                    if (source is LocalStorageFiles localSource)
                    {
                        var volume = StorageFileSystem.DownloadFilesystemIdentity(localSource.ResolveDirectory(sourceDirectory), includeDirectory: false, includeMountPoint: true);
                        canLink = volume is not null && volume == StorageFileSystem.DownloadFilesystemIdentity(targetDirectory, includeDirectory: false, includeMountPoint: true);
                    }
                }
                else
                {
                    freeBytes = (await library.ProbeAsync(ct)).FreeBytes;
                }

                var lengths = files.ToDictionary(file => file.RelativePath, file => file.SizeBytes, StringComparer.Ordinal);
                var copyBytes = canLink ? 0 : placements.Sum(placement => lengths[placement.SourcePath]);
                if (freeBytes is null || copyBytes > freeBytes.Value)
                {
                    return new ImportCommit("import_storage_full", []);
                }

                foreach (var group in placements.GroupBy(placement => placement.Group))
                {
                    var stem = await FreeStemAsync(library, directory, group.First().TargetStem, group.Select(placement => placement.Extension).ToArray(), assigned, ct);
                    if (stem is null)
                    {
                        // No numbered name is free: nothing is written over an existing library file, and what this call placed so far goes.
                        await RollbackAsync(library, directory, imported);
                        return new ImportCommit("import_target_conflict", []);
                    }

                    foreach (var placement in group)
                    {
                        var relativePath = stem + placement.Extension;
                        var placed = await StorageFileTransfer.PlaceAsync(source, StoragePath.Combine(sourceDirectory, placement.SourcePath), library, StoragePath.Combine(directory, relativePath), ct);
                        imported.Add(new ImportedFile(relativePath, placed.Length, StoredFileLogic.StoredTime(placed.ModifiedAt), request.FactsFor(placement.SourcePath), request.Languages?.GetValueOrDefault(placement.SourcePath)));
                        targets[placement.SourcePath] = libraryName + " /" + StoragePath.Combine(directory, relativePath);
                        if (placement.IsMedia)
                        {
                            media.Add((relativePath, placement));
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                // What this call already placed goes again, so a retry does not find its own leftovers and name the files anew.
                await RollbackAsync(library, directory, imported);
                return new ImportCommit("import_storage_unavailable", []);
            }

            if (media.Count == 0)
            {
                await RollbackAsync(library, directory, imported);
                return new ImportCommit("import_assignment_failed", []);
            }

            IReadOnlyList<long> assets;
            try
            {
                assets = await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.UpsertStoredFilesAsync(new StoredFileBatchData(root.Id, StoredFileLogic.StoredTime(DateTime.UtcNow), JsonSerializer.Serialize(imported)), token);
                    var assigned = new List<long>();
                    foreach (var (relativePath, placement) in media)
                    {
                        var file = await ctx.ReadStoredFileIdAsync(new LibraryFilePathData(root.Id, relativePath), token);
                        if (file is null)
                        {
                            // Never complete an import if the library inventory did not accept a media file.
                            throw new ImportAssignmentException();
                        }

                        var assignment = new StoredFileAssignmentData(file.Id, request.WorkId, null, placement.WorkUnitTypeId, placement.UnitNumber,
                            placement.ParentUnitNumber, request.GamePlatformId, false);
                        assigned.Add((placement.FurtherEpisodes.Count > 0
                            ? await ctx.AssignStoredFileRangeAsync(assignment, placement.FurtherEpisodes, token)
                            : await ctx.AssignStoredFileAsync(assignment, token)).Id);

                        // An import by itself is recorded with its operation, one by hand with the person who made it (and the download it was made for).
                        await ctx.RecordActivityAsync(request.AccountId is { } accountId ? new ActivityEventData(ActivityEventType.ManualImport, operationId: request.OperationId, storedFileId: file.Id, accountId: accountId)
                            : new ActivityEventData(ActivityEventType.Imported, operationId: request.OperationId, storedFileId: file.Id), token);
                    }

                    // The release counts as imported once a file of it is in the library, also while a person still settles the rest of its download.
                    if (request.OperationId is { } finished && request.FinishesDownload)
                    {
                        await ctx.CompleteDownloadAsync(new DownloadOperationParameters(finished), token);
                    }
                    else if (request.OperationId is { } waiting)
                    {
                        await ctx.MarkDownloadReleaseImportedAsync(new DownloadOperationParameters(waiting), token);
                    }

                    // The scan analyses the new streams.
                    await ctx.QueueLibraryScanAsync(new OperationCreationData(OperationKind.LibraryScan, root.Id, null), token);
                    return assigned;
                }, ct);
            }
            catch (ImportAssignmentException)
            {
                // The database transaction rolls back; the files this call placed go, the download stays for manual-safe recovery.
                await RollbackAsync(library, directory, imported);
                return new ImportCommit("import_assignment_failed", []);
            }
            catch
            {
                await RollbackAsync(library, directory, imported);
                throw;
            }

            // The move ends: the sources go, but only once the library holds the files.
            var kept = await RemoveSourcesAsync(request, ct);

            await replacements.ReplaceAsync(context, assets, request.Profile, ct);

            // The scan that analyses the new streams starts at once.
            signals.Scans.Notify();

            // A connected Plex shows the title right away.
            await plex.RefreshAsync(context, request.MediaTypeId, ct);
            return new ImportCommit(null, assets, targets, kept);
        }

        // The files this import itself placed, which did not exist before it chose their names, and the folders that leaves empty. A source is never touched.
        private static async Task RollbackAsync(StorageFiles library, string directory, IReadOnlyList<ImportedFile> placed)
        {
            try
            {
                foreach (var file in placed)
                {
                    await library.DeleteFileAsync(StoragePath.Combine(directory, file.RelativePath), CancellationToken.None);
                }

                foreach (var folder in placed.Select(file => StoragePath.Parent(file.RelativePath)).Where(folder => folder.Length > 0).Distinct(StringComparer.Ordinal))
                {
                    await library.DeleteEmptyFoldersAsync(StoragePath.Combine(directory, folder), directory, CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
            }
        }

        // What the move took from its folder goes, and so do the folders it emptied; the sources that could not be deleted (a read-only connection) stay and are named.
        private static async Task<IReadOnlyList<string>> RemoveSourcesAsync(ImportCommitRequest request, CancellationToken ct)
        {
            var kept = new List<string>();
            foreach (var placement in request.Placements)
            {
                try
                {
                    await request.Source.DeleteFileAsync(StoragePath.Combine(request.SourceDirectory, placement.SourcePath), CancellationToken.None);
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                {
                    kept.Add(placement.SourcePath);
                }
            }

            try
            {
                foreach (var folder in request.Placements.Select(placement => StoragePath.Parent(placement.SourcePath)).Distinct(StringComparer.Ordinal))
                {
                    await request.Source.DeleteEmptyFoldersAsync(StoragePath.Combine(request.SourceDirectory, folder), request.SourceDirectory, ct);
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
            }

            return kept;
        }

        // A completed import records a durable cleanup request once the move left nothing of the job that an import would take (only release notes, samples, extras or subtitles
        // nobody chose): SABnzbd removes the job and its verified leftovers, qBittorrent the torrent, which could not seed its moved files anyway. The worker owns remote deletion and retry.
        // A single file has no folder of its own to check; its client lets go of it once it is moved.
        public async Task RemoveFromClientAsync(LogicContext context, long acquisitionConnectionId, string? externalId, StorageFiles source, string directory, bool isFolder, CancellationToken ct)
        {
            if (externalId is null) return;
            var fingerprint = DownloadSourceInventory.Empty;
            if (isFolder)
            {
                var left = new List<StorageFile>();
                var state = new StorageScanState();
                try
                {
                    if (await source.GetAsync(directory, ct) is { IsDirectory: true })
                    {
                        await foreach (var file in source.EnumerateFilesAsync(directory, null, state, ct)) left.Add(file);
                    }
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                {
                    return;
                }

                if (!state.IsComplete || ManualImportLogic.HoldsMedia(left)) return;
                fingerprint = DownloadSourceInventory.Fingerprint(left);
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.QueueImportedCleanupAsync(new DownloadCleanupKeyData(acquisitionConnectionId, externalId, sourceFingerprint: fingerprint), token);
                return true;
            }, ct);
            signals.Downloads.Notify();
        }

        // Retry only terminal history jobs. This never removes a job that remains in SABnzbd's active queue.
        public async Task<int> ProcessCleanupAsync(LogicContext context, CancellationToken ct)
        {
            // Downloads that wait for a person although their folder holds nothing to assign are failed as a new one would be, once, here.
            foreach (var stuck in await context.ExecuteBatchAsync((ctx, token) => ctx.ReadUnassignableDownloadsAsync(token), ct))
            {
                await FailAsync(context, stuck.OperationId, stuck.IsEmpty ? ImportJudgement.EmptyFolder : ImportJudgement.NoFile, time.GetUtcNow().UtcDateTime, ct);
            }

            // Reconcile the durable download outcome after an interrupted enqueue; failed downloads still in the queue are settled as new failures are.
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.QueueMissedDownloadCleanupsAsync(token);
                await ctx.SettleFailedDownloadsAsync(token);
                return true;
            }, ct);
            var due = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDueDownloadCleanupsAsync(token), ct);
            var processed = 0;
            foreach (var task in due)
            {
                if (!await context.ExecuteBatchAsync((ctx, token) => ctx.ClaimDownloadCleanupAsync(new DownloadCleanupAttemptData(task.Id), token), ct))
                    continue;

                processed++;
                try
                {
                    var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(task.AcquisitionConnectionId, token), ct);

                    // A failed release elsewhere goes with its files too: qBittorrent deletes the torrent and its data, Jularr's own Usenet and torrent downloaders clear the job's folders.
                    // An imported torrent goes from qBittorrent with what the move left of it.
                    if (connection.AcquisitionConnectionTypeId is AcquisitionConnectionType.Qbittorrent or AcquisitionConnectionType.Usenet or AcquisitionConnectionType.Torrent)
                    {
                        if (task.Reason != "failed" && !(task.Reason == "imported" && connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent))
                        {
                            await RescheduleAsync(task.Id, "cleanup_status_mismatch");
                            continue;
                        }

                        if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent)
                        {
                            // A client without a login of its own (a trusted network) has no password stored.
                            var password = connection.ProtectedPassword is null ? null
                                : AcquisitionConnectionInput.Unprotect(protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose), connection.ProtectedPassword);
                            await qbittorrent.RemoveAsync(new QbittorrentConnection(connection.HttpAddress, connection.Username, password), task.ExternalId, true, ct);
                        }
                        else if (task.OperationId is { } operationId)
                        {
                            var area = connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet ? DownloadStorageArea.Usenet : DownloadStorageArea.Torrent;
                            (await downloads.OpenAsync(context, area, ct, skipCapacityCheck: true)).RemoveCompleted(operationId);
                        }

                        await context.ExecuteBatchAsync(async (ctx, token) =>
                        {
                            await ctx.MarkDownloadCleanupDoneAsync(new DownloadCleanupAttemptData(task.Id), token);
                            return true;
                        }, ct);
                        continue;
                    }

                    if (connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd)
                    {
                        await RescheduleAsync(task.Id, "cleanup_invalid_client");
                        continue;
                    }

                    var key = AcquisitionConnectionInput.Unprotect(protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose), connection.ProtectedApiKey);
                    var client = new SabnzbdConnection(connection.HttpAddress, key);
                    var queued = await sabnzbd.GetQueueAsync(client, [task.ExternalId], ct);
                    if (queued.Jobs.Count != 0)
                    {
                        await RescheduleAsync(task.Id, "cleanup_still_active");
                        continue;
                    }

                    var history = await sabnzbd.GetHistoryAsync(client, [task.ExternalId], ct);
                    var finished = history.Jobs.FirstOrDefault(job => job.NzoId == task.ExternalId);
                    // A missing history entry was removed externally, so there is no job left to clean.
                    if (finished is not null)
                    {
                        if (task.Reason == "imported" && !finished.IsCompleted
                            || task.Reason == "failed" && !finished.IsFailed
                            || task.Reason is not ("imported" or "failed"))
                        {
                            await RescheduleAsync(task.Id, "cleanup_status_mismatch");
                            continue;
                        }

                        if (task.Reason == "imported" && (task.SourceFingerprint is null
                            || !await DeleteImportedSourceAsync(context, connection, finished.StoragePath, task.SourceFingerprint, ct)))
                        {
                            // A changed folder (including additional files) is never auto-deleted.
                            // Old unverified rows remain visible for manual resolution.
                            await RescheduleAsync(task.Id, "cleanup_source_unverified");
                            continue;
                        }

                        // Imported source files were already verified and removed locally.
                        // Do not ask SABnzbd to delete that path again: a client may have
                        // recreated it between the atomic local rename and history removal.
                        await sabnzbd.RemoveHistoryJobAsync(client, task.ExternalId, task.Reason != "imported", ct);
                    }

                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.MarkDownloadCleanupDoneAsync(new DownloadCleanupAttemptData(task.Id), token);
                        return true;
                    }, ct);
                }
                catch (DataValidationException)
                {
                    // The provider directory or its inventory changed. Preserve source data
                    // and leave the durable cleanup available for explicit reconciliation.
                    await RescheduleAsync(task.Id, "cleanup_source_unverified");
                }
                catch (Exception exception) when (exception is HttpRequestException or SabnzbdException or QbittorrentException or JsonException
                    or IOException or UnauthorizedAccessException or NasMountException or ServiceUnavailableException
                    || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    await RescheduleAsync(task.Id, "cleanup_client_unavailable");
                }
            }

            return processed;

            Task RescheduleAsync(long id, string reason) => context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.RetryDownloadCleanupAsync(new DownloadCleanupAttemptData(id, reason), token);
                return true;
            }, ct);
        }

        // A finished SABnzbd job can keep its completed source files after a successful hard-link import.
        // Only the provider-reported path under the explicitly mapped download root is eligible.
        private async Task<bool> DeleteImportedSourceAsync(LogicContext context, AcquisitionConnectionData connection,
            string? reportedPath, string sourceFingerprint, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(reportedPath) || string.IsNullOrWhiteSpace(connection.RemotePath)
                || ImportPlacementLogic.MapRemotePath(reportedPath, connection.RemotePath) is not { Length: > 0 } relative
                || !connection.RemovesCompleted)
                return false;

            string root;
            if (connection.DownloadStorageMountId is { } mountId)
            {
                if (string.IsNullOrWhiteSpace(connection.LocalPath)) return false;
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(mountId, token), ct);
                if (mount.IsReadOnly || storage.Open(mount) is not LocalStorageFiles local) return false;
                root = local.ResolveDirectory(StoragePath.Check(connection.LocalPath));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(connection.LocalPath) || !Path.IsPathFullyQualified(connection.LocalPath))
                    return false;
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(connection.LocalPath));
                if (root == Path.GetPathRoot(root) || !Directory.Exists(root))
                    return false;
            }

            return DownloadClientFileSafety.DeleteVerifiedCompleted(root, relative, sourceFingerprint);
        }

        // The first numbered alternative whose files are all free on the library and in its inventory.
        internal static async Task<string?> FreeStemAsync(StorageFiles library, string directory, string stem, IReadOnlyList<string> extensions, IReadOnlySet<string> assigned, CancellationToken ct)
        {
            for (var attempt = 1; attempt <= 99; attempt++)
            {
                var candidate = LibraryNaming.Alternative(stem, attempt);
                var free = true;
                foreach (var extension in extensions)
                {
                    free &= !assigned.Contains(candidate + extension) && !await library.ExistsAsync(StoragePath.Combine(directory, candidate + extension), ct);
                }

                if (free)
                {
                    return candidate;
                }
            }

            return null;
        }

        // Every file of the download, also those of a format the import does not take; the import's own files when the folder cannot be read again.
        private static async Task<IReadOnlyList<StorageFile>> ListAsync(StorageFiles source, string directory, IReadOnlyList<StorageFile> files, CancellationToken ct)
        {
            var state = new StorageScanState();
            var found = new List<StorageFile>();
            try
            {
                await foreach (var file in source.EnumerateFilesAsync(directory, null, state, ct))
                {
                    found.Add(file);
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                return files;
            }

            return state.IsComplete ? found : files;
        }

        public Task AttentionAsync(LogicContext context, long operationId, string attentionKey, string? attentionUrl, CancellationToken ct, string? attentionProposal = null) =>
            context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SetDownloadAttentionAsync(new DownloadAttentionData(operationId, attentionKey, attentionUrl, attentionProposal), token);
                return true;
            }, ct);

        public Task FailAsync(LogicContext context, long operationId, string reasonKey, DateTime nextSearchAt, CancellationToken ct) =>
            context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.FailDownloadAsync(new DownloadFailureData(operationId, reasonKey, nextSearchAt), token);
                await ctx.QueueFailedCleanupAsync(new DownloadCleanupOperationData(operationId), token);
                return true;
            }, ct);

        private sealed class ImportAssignmentException : Exception
        {
        }

        private sealed class ImportedFile
        {
            public ImportedFile(string relativePath, long sizeBytes, DateTime modifiedAt, ReleaseFact? facts, ChosenLanguages? languages)
            {
                ChosenAudioLanguages = languages is { Audio.Length: > 0 } ? languages.Audio : null;
                ChosenSubtitleLanguages = languages is { Subtitles.Length: > 0 } ? languages.Subtitles : null;
                RelativePath = relativePath;
                SizeBytes = sizeBytes;
                ModifiedAt = modifiedAt;
                ReleaseName = facts?.ReleaseName;
                ReleaseGroup = facts?.ReleaseGroup;
                QualityKey = facts?.QualityKey;
                Edition = facts?.Edition;
                Revision = facts?.Revision ?? 1;
            }

            public string RelativePath { get; }
            public long SizeBytes { get; }
            public DateTime ModifiedAt { get; }
            public string? ReleaseName { get; }
            public string? ReleaseGroup { get; }
            public string? QualityKey { get; }
            public string? Edition { get; }
            public int Revision { get; }
            public string[]? ChosenAudioLanguages { get; }
            public string[]? ChosenSubtitleLanguages { get; }
        }
    }

    // What an import places and where it takes it from; a download names its operation, a manual import the person who made it. Languages, by source path, are the ones a person chose for a file.
    // An import by hand of a download leaves it waiting (FinishesDownload false) until its last group of files is placed or the person finishes it.
    internal sealed record ImportCommitRequest(LibraryRootLocationData Root, long WorkId, long? GamePlatformId, IReadOnlyList<ImportPlacementData> Placements, StorageFiles Source,
        string SourceDirectory, IReadOnlyList<StorageFile> Files, Func<string, ReleaseFact?> FactsFor, long? OperationId, long? AccountId, MediaType MediaTypeId,
        QualityProfile? Profile, IReadOnlyDictionary<string, ChosenLanguages>? Languages = null, bool FinishesDownload = true);

    // The reason a person must settle, or null once the library holds the files; then where each source went (by its path, the path inside the library's storage) and the sources
    // that could not be deleted after the library took them.
    internal sealed record ImportCommit(string? FailureKey, IReadOnlyList<long> Assets, IReadOnlyDictionary<string, string>? Targets = null, IReadOnlyList<string>? KeptSources = null);
}
