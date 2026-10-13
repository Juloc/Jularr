using System.Text.Json;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // Shared by every downloader: plan, check, link or copy and assign; waits and failures are recorded the same way.
    internal sealed class DownloadImport(PlexServer plex, IOptions<StorageOptions> options, TimeProvider time)
    {
        public async Task PlaceAsync(LogicContext context, DownloadData download, string source, IReadOnlyList<StorageFile> files, bool isComplete, CancellationToken ct)
        {
            var root = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadImportRootAsync(download.WantedItemId, token), ct);
            if (root is null)
            {
                await AttentionAsync(context, download.OperationId, "import_no_library", null, ct);
                return;
            }

            // Ebooks and comics must be what their names say.
            var placements = ImportPlacementLogic.Plan(download.MediaTypeId, download.MediaSubtypeId, download.CanonicalTitle, download.ReleaseYear, download.QualityKey, files, download.Author, download.TrackCount, download.PlatformName)
                .Where(placement => !placement.IsMedia || LibraryFileTypes.GetAssetType(placement.SourcePath) is not (MediaAssetType.Ebook or MediaAssetType.Comic) || IsDocument(source, placement))
                .ToArray();
            if (placements.Length == 0)
            {
                if (isComplete)
                {
                    await FailAsync(context, download.OperationId, DownloadLogic.FailedReasonKey, time.GetUtcNow().UtcDateTime, ct);
                }

                return;
            }

            // Link or copy outside transactions; an existing library file is never taken over.
            // An offline library keeps the download waiting instead of writing elsewhere.
            string directory;
            try
            {
                directory = StorageFileSystem.ResolveDirectory(options.Value.GetConnectionPath(root.StorageProtocolTypeId, root.StorageMountId, root.SharePath), root.RelativePath);
            }
            catch (NasMountException)
            {
                await AttentionAsync(context, download.OperationId, "import_library_unavailable", null, ct);
                return;
            }

            var imported = new List<ImportedFile>();
            var media = new List<(string RelativePath, ImportPlacementData Placement)>();
            foreach (var group in placements.GroupBy(placement => placement.Group))
            {
                var stem = Enumerable.Range(1, 99).Select(attempt => ImportPlacementLogic.Alternative(group.First().TargetStem, attempt))
                    .First(candidate => group.All(placement => !File.Exists(Path.Combine(directory, (candidate + placement.Extension).Replace('/', Path.DirectorySeparatorChar)))));
                foreach (var placement in group)
                {
                    var relativePath = stem + placement.Extension;
                    var folder = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                    Directory.CreateDirectory(Path.Combine(directory, folder));
                    var destination = Path.Combine(StorageFileSystem.ResolveDirectory(directory, folder), Path.GetFileName(relativePath));
                    var sourceFile = StorageFileSystem.ResolveFile(source, placement.SourcePath);
                    await Task.Run(() => StorageFileTransfer.HardlinkOrCopy(sourceFile, destination), ct);

                    var info = new FileInfo(destination);
                    imported.Add(new ImportedFile(relativePath, info.Length, Truncate(info.LastWriteTimeUtc)));
                    if (placement.IsMedia)
                    {
                        media.Add((relativePath, placement));
                    }
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.UpsertStoredFilesAsync(new StoredFileBatchData(root.Id, Truncate(DateTime.UtcNow), JsonSerializer.Serialize(imported)), token);
                foreach (var (relativePath, placement) in media)
                {
                    if (await ctx.ReadStoredFileIdAsync(new LibraryFilePathData(root.Id, relativePath), token) is { } file)
                    {
                        var asset = await ctx.AssignStoredFileAsync(new StoredFileAssignmentData(file.Id, download.WorkId, null, placement.WorkUnitTypeId, placement.UnitNumber, placement.ParentUnitNumber), token);
                        if (download.GamePlatformId is { } platform)
                        {
                            await ctx.TagGameReleaseAsync(new GameReleaseData(asset.Id, platform), token);
                        }
                    }
                }

                await ctx.CompleteDownloadAsync(new DownloadOperationParameters(download.OperationId), token);

                // The scan analyses the new streams.
                await ctx.QueueLibraryScanAsync(new OperationCreationData(OperationKind.LibraryScan, root.Id, null), token);
                return true;
            }, ct);

            // A connected Plex shows the title right away.
            await plex.RefreshAsync(context, download.MediaTypeId, ct);
        }

        private static bool IsDocument(string source, ImportPlacementData placement)
        {
            var header = new byte[128];
            using var stream = File.OpenRead(StorageFileSystem.ResolveFile(source, placement.SourcePath));
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return LibraryFileTypes.IsDocumentContent(placement.Extension, header.AsSpan(0, read));
        }

        public Task AttentionAsync(LogicContext context, long operationId, string attentionKey, string? attentionUrl, CancellationToken ct) =>
            context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SetDownloadAttentionAsync(new DownloadAttentionData(operationId, attentionKey, attentionUrl), token);
                return true;
            }, ct);

        public Task FailAsync(LogicContext context, long operationId, string reasonKey, DateTime nextSearchAt, CancellationToken ct) =>
            context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.FailDownloadAsync(new DownloadFailureData(operationId, reasonKey, nextSearchAt), token);
                return true;
            }, ct);

        private static DateTime Truncate(DateTime value) => DateTime.UnixEpoch.AddTicks((value.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 10);

        private sealed class ImportedFile
        {
            public ImportedFile(string relativePath, long sizeBytes, DateTime modifiedAt)
            {
                RelativePath = relativePath;
                SizeBytes = sizeBytes;
                ModifiedAt = modifiedAt;
            }

            public string RelativePath { get; }
            public long SizeBytes { get; }
            public DateTime ModifiedAt { get; }
        }
    }
}
