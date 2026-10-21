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
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // Shared by every downloader: plan, check, link or copy and assign; waits and failures are recorded the same way.
    internal sealed class DownloadImport(PlexServer plex, LibraryReplacements replacements, StorageAccess storage, TimeProvider time, SabnzbdClient sabnzbd, IDataProtectionProvider protection, WorkSignals signals)
    {
        public async Task PlaceAsync(LogicContext context, DownloadData download, StorageFiles source, ImportSourceRef sourceRef, IReadOnlyList<StorageFile> files, bool isComplete, CancellationToken ct)
        {
            var sourceDirectory = sourceRef.Directory;
            if (!isComplete)
            {
                await AttentionAsync(context, download.OperationId, "import_source_unavailable", null, ct);
                return;
            }

            var (root, patterns, target, units) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadImportRootAsync(download.WantedItemId, token), await ctx.ReadNamingPatternsAsync(token), await ctx.ReadWantedTargetAsync(download.WantedItemId, token),
                    download.MediaTypeId == MediaType.Series ? await ctx.ReadUnitStatesAsync(download.WantedItemId, token) : []), ct);
            if (root is null)
            {
                await AttentionAsync(context, download.OperationId, "import_no_library", null, ct);
                return;
            }

            var parsedFacts = ReleaseFacts.Of(download.Title, download.MediaTypeId == MediaType.Movie);
            var facts = new ReleaseFact(TextLimit.Cut(download.Title, 500)!, parsedFacts?.ReleaseGroup, download.QualityKey ?? parsedFacts?.QualityKey,
                parsedFacts?.Edition, parsedFacts?.Revision ?? 1);

            // A clear film or episode imports by itself, one of no use is excluded and searched again at once, a doubt waits for a person with its reason.
            var verdict = ImportJudgement.Judge(download.MediaTypeId, download.MediaSubtypeId, download.CanonicalTitle, download.ReleaseYear, download.Title, download.ExpectedParentUnitNumber,
                download.ExpectedUnitStart, download.ExpectedUnitEnd, files, EpisodeNumbering.Episodes(units), target.Definition is null ? null : AcquisitionSelectionLogic.ReadProfile(target));
            if (verdict.Kind == ImportVerdictKind.Discard)
            {
                await FailAsync(context, download.OperationId, verdict.ReasonKey!, time.GetUtcNow().UtcDateTime, ct);
                return;
            }

            if (verdict.Kind == ImportVerdictKind.Doubt)
            {
                await AttentionAsync(context, download.OperationId, verdict.ReasonKey!, null, ct, ImportProposal.Write(sourceRef, verdict.Proposal));
                return;
            }

            // Music follows its tags; ebooks and comics must be what their names say.
            Dictionary<string, AudioTags>? tags = download.MediaTypeId == MediaType.Music ? new(StringComparer.Ordinal) : null;
            var placements = new List<ImportPlacementData>();
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

                foreach (var placement in ImportPlacementLogic.Plan(download.MediaTypeId, download.MediaSubtypeId, download.CanonicalTitle, download.ReleaseYear, download.QualityKey,
                    files, patterns, facts, download.Author, download.TrackCount, download.PlatformName, EpisodeNumbering.Episodes(units), tags))
                {
                    if (!placement.IsMedia || LibraryFileTypes.GetAssetType(placement.SourcePath) is not (MediaAssetType.Ebook or MediaAssetType.Comic)
                        || await IsDocumentAsync(source, StoragePath.Combine(sourceDirectory, placement.SourcePath), placement.Extension, ct))
                    {
                        placements.Add(placement);
                    }
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                await AttentionAsync(context, download.OperationId, "import_source_unavailable", null, ct);
                return;
            }

            if (placements.Count == 0)
            {
                if (isComplete)
                {
                    await FailAsync(context, download.OperationId, DownloadLogic.FailedReasonKey, time.GetUtcNow().UtcDateTime, ct);
                }

                return;
            }

            var commit = await CommitAsync(context, new ImportCommitRequest(root, download.WorkId, download.GamePlatformId, placements, source, sourceDirectory, files, _ => facts,
                ImportTransferMode.Link, download.OperationId, null, download.MediaTypeId, target.Definition is null ? null : AcquisitionSelectionLogic.ReadProfile(target)), ct);
            if (commit.FailureKey is { } failure)
            {
                await AttentionAsync(context, download.OperationId, failure, null, ct);
                return;
            }

            // Its job leaves SABnzbd with its files, when the connection is set to; a library file placed by link stays.
            await RemoveFromClientAsync(context, download.AcquisitionConnectionId, download.ExternalId, ct);
        }

        // Links, copies or moves the planned files into the library and assigns them to their work; the failure key names what a person must settle, and nothing is left half done.
        public async Task<ImportCommit> CommitAsync(LogicContext context, ImportCommitRequest request, CancellationToken ct)
        {
            // Link, copy or move outside transactions; an existing library file is never taken over.
            // An offline library keeps the download waiting instead of writing elsewhere.
            var (root, placements, source, sourceDirectory, files) = (request.Root, request.Placements, request.Source, request.SourceDirectory, request.Files);
            var directory = root.RelativePath;
            StorageFiles library;
            try
            {
                var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(root.StorageMountId, token), ct);
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

            var imported = new List<ImportedFile>();
            var media = new List<(string RelativePath, ImportPlacementData Placement)>();
            try
            {
                var canLink = false;
                long? freeBytes;
                if (library is LocalStorageFiles local)
                {
                    var targetDirectory = local.ResolveDirectory(directory);
                    freeBytes = StorageFileSystem.AvailableFreeSpace(targetDirectory);
                    if (source is LocalStorageFiles localSource && request.Mode != ImportTransferMode.Copy)
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
                    var stem = await FreeStemAsync(library, directory, group.First().TargetStem, group.Select(placement => placement.Extension).ToArray(), ct);
                    if (stem is null)
                    {
                        // No numbered name is free: nothing is written over an existing library file, and what this call placed so far goes.
                        await RollbackAsync(library, directory, imported);
                        return new ImportCommit("import_target_conflict", []);
                    }

                    foreach (var placement in group)
                    {
                        var relativePath = stem + placement.Extension;
                        var placed = await StorageFileTransfer.PlaceAsync(source, StoragePath.Combine(sourceDirectory, placement.SourcePath), library, StoragePath.Combine(directory, relativePath), ct,
                            allowLink: request.Mode != ImportTransferMode.Copy);
                        imported.Add(new ImportedFile(relativePath, placed.Length, StoredFileLogic.StoredTime(placed.ModifiedAt), request.FactsFor(placement.SourcePath)));
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

                        assigned.Add((await ctx.AssignStoredFileAsync(new StoredFileAssignmentData(file.Id, request.WorkId, null, placement.WorkUnitTypeId, placement.UnitNumber,
                            placement.ParentUnitNumber, request.GamePlatformId, false), token)).Id);

                        // A download's import is recorded with its operation, a manual one with the person who made it.
                        await ctx.RecordActivityAsync(request.OperationId is { } operationId ? new ActivityEventData(ActivityEventType.Imported, operationId: operationId, storedFileId: file.Id)
                            : new ActivityEventData(ActivityEventType.ManualImport, storedFileId: file.Id, accountId: request.AccountId), token);
                    }

                    if (request.OperationId is { } finished)
                    {
                        await ctx.CompleteDownloadAsync(new DownloadOperationParameters(finished), token);
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

            // A move leaves nothing behind, but only once the library holds the files.
            if (request.Mode == ImportTransferMode.Move)
            {
                await RemoveSourcesAsync(request, ct);
            }

            await replacements.ReplaceAsync(context, assets, request.Profile, ct);

            // The scan that analyses the new streams starts at once.
            signals.Scans.Notify();

            // A connected Plex shows the title right away.
            await plex.RefreshAsync(context, request.MediaTypeId, ct);
            return new ImportCommit(null, assets);
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

        // What a move took from its folder goes, and so do the folders it emptied; a source that cannot be changed keeps its files.
        private static async Task RemoveSourcesAsync(ImportCommitRequest request, CancellationToken ct)
        {
            try
            {
                foreach (var placement in request.Placements)
                {
                    await request.Source.DeleteFileAsync(StoragePath.Combine(request.SourceDirectory, placement.SourcePath), ct);
                }

                foreach (var folder in request.Placements.Select(placement => StoragePath.Parent(placement.SourcePath)).Distinct(StringComparer.Ordinal))
                {
                    await request.Source.DeleteEmptyFoldersAsync(StoragePath.Combine(request.SourceDirectory, folder), request.SourceDirectory, ct);
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
            }
        }

        // Tidying is no reason to fail an import that worked: a client that cannot be reached keeps the job.
        public async Task RemoveFromClientAsync(LogicContext context, long acquisitionConnectionId, string? externalId, CancellationToken ct)
        {
            try
            {
                var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(acquisitionConnectionId, token), ct);
                if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Sabnzbd && connection.RemovesCompleted && externalId is { } nzoId)
                {
                    var key = AcquisitionConnectionInput.Unprotect(protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose), connection.ProtectedApiKey);
                    await sabnzbd.RemoveJobAsync(new SabnzbdConnection(connection.HttpAddress, key), nzoId, true, ct);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or SabnzbdException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
            }
        }

        // The first numbered alternative whose files are all free.
        internal static async Task<string?> FreeStemAsync(StorageFiles library, string directory, string stem, IReadOnlyList<string> extensions, CancellationToken ct)
        {
            for (var attempt = 1; attempt <= 99; attempt++)
            {
                var candidate = LibraryNaming.Alternative(stem, attempt);
                var free = true;
                foreach (var extension in extensions)
                {
                    free &= !await library.ExistsAsync(StoragePath.Combine(directory, candidate + extension), ct);
                }

                if (free)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static async Task<bool> IsDocumentAsync(StorageFiles source, string path, string extension, CancellationToken ct)
        {
            var header = new byte[128];
            await using var stream = await source.OpenReadAsync(path, ct);
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            return LibraryFileTypes.IsDocumentContent(extension, header.AsSpan(0, read));
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
                return true;
            }, ct);

        private sealed class ImportAssignmentException : Exception
        {
        }

        private sealed class ImportedFile
        {
            public ImportedFile(string relativePath, long sizeBytes, DateTime modifiedAt, ReleaseFact? facts)
            {
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
        }
    }

    // What an import places and where it takes it from; a download names its operation, a manual import the person who made it.
    internal sealed record ImportCommitRequest(LibraryRootLocationData Root, long WorkId, long? GamePlatformId, IReadOnlyList<ImportPlacementData> Placements, StorageFiles Source,
        string SourceDirectory, IReadOnlyList<StorageFile> Files, Func<string, ReleaseFact?> FactsFor, ImportTransferMode Mode, long? OperationId, long? AccountId, MediaType MediaTypeId,
        QualityProfile? Profile);

    // The reason a person must settle, or null once the library holds the files.
    internal sealed record ImportCommit(string? FailureKey, IReadOnlyList<long> Assets);
}
