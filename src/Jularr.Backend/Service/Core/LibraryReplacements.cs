using System.Text.Json;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // Earlier files of the same film, episode, track or volume leave the library: after an import when the new release is better,
    // or when an administrator keeps only one version. Several kept releases stay.
    internal sealed class LibraryReplacements
    {
        private readonly StorageAccess _storage;
        private readonly ILogger<LibraryReplacements> _logger;

        public LibraryReplacements(StorageAccess storage, ILogger<LibraryReplacements> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        // After an import; without a profile nothing is known to be better.
        public async Task ReplaceAsync(LogicContext context, IReadOnlyList<long> newMediaAssetIds, QualityProfile? profile, CancellationToken ct)
        {
            if (newMediaAssetIds.Count == 0 || profile is null)
            {
                return;
            }

            var (settings, files) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadReplacementSettingsAsync(token), await ctx.ReadReplacedFilesAsync(new ReplacingAssetsData([.. newMediaAssetIds], false), token)), ct);
            if (!settings.KeepsReleases)
            {
                await RetireAsync(context, settings, files.Where(file => ReplacementLogic.IsBetter(profile, file)).ToArray(), ActivityEventType.Upgraded, null, ct);
            }
        }

        // An administrator's choice: every other version of the same kind, edition and format goes, whatever its quality.
        public async Task KeepOnlyAsync(LogicContext context, long mediaAssetId, long accountId, CancellationToken ct)
        {
            var (settings, files) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadReplacementSettingsAsync(token), await ctx.ReadReplacedFilesAsync(new ReplacingAssetsData([mediaAssetId], true), token)), ct);
            await RetireAsync(context, settings, files, ActivityEventType.FileDeleted, accountId, ct);
        }

        // An administrator deletes files: each goes as a replaced file does (deleted or recycled); the unit is missing again and its target is searched at once.
        // A file that cannot be removed stays and is counted as kept. Subtitles beside a chosen media file go with it.
        public async Task<(int Deleted, int Kept)> DeleteAsync(LogicContext context, IReadOnlyList<long> storedFileIds, long accountId, CancellationToken ct)
        {
            var (settings, files) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadReplacementSettingsAsync(token), await ctx.ReadDeletableFilesAsync(new DeletableFilesQueryData([.. storedFileIds]), token)), ct);
            var gone = await MoveOutAsync(context, settings, files.Select(file => new FileToRetire(file.StoredFileId, null, file.StorageMountId, file.RootPath, file.RelativePath, file.IsMissing)), ct);
            if (gone.Count > 0)
            {
                var chosen = files.Where(file => file.IsMedia || storedFileIds.Contains(file.StoredFileId)).Select(file => file.StoredFileId).ToHashSet();
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    foreach (var retired in gone.Where(item => chosen.Contains(item.StoredFileId)))
                    {
                        await ctx.RecordActivityAsync(new ActivityEventData(ActivityEventType.FileDeleted, storedFileId: retired.StoredFileId, accountId: accountId), token);
                    }

                    await ctx.ForgetDeletedFilesAsync(new DeletedFilesData(JsonSerializer.Serialize(gone), settings.UnmonitorsDeletedUnits), token);
                    return true;
                }, ct);
            }

            return (gone.Count, files.Count - gone.Count);
        }

        // A file that cannot be moved, on an unreachable library or with an unusable recycle folder, stays as a further version. The history names each file that went.
        private async Task RetireAsync(LogicContext context, ReplacementSettingsData settings, IReadOnlyList<ReplacedFileData> files, ActivityEventType eventType, long? accountId,
            CancellationToken ct)
        {
            var retired = await MoveOutAsync(context, settings, files.DistinctBy(file => file.StoredFileId)
                .Select(file => new FileToRetire(file.StoredFileId, file.NewMediaAssetId, file.StorageMountId, file.RootPath, file.RelativePath, false)), ct);
            if (retired.Count > 0)
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    foreach (var file in retired)
                    {
                        await ctx.RecordActivityAsync(new ActivityEventData(eventType, storedFileId: file.StoredFileId, accountId: accountId), token);
                    }

                    await ctx.RetireReplacedFilesAsync(new RetiredFilesData(JsonSerializer.Serialize(retired)), token);
                    return true;
                }, ct);
            }
        }

        // Deletes or recycles each file as configured; the files that went are returned, the others stay where they are.
        private async Task<List<RetiredFile>> MoveOutAsync(LogicContext context, ReplacementSettingsData settings, IEnumerable<FileToRetire> files, CancellationToken ct)
        {
            var retired = new List<RetiredFile>();
            var connections = new Dictionary<long, StorageFiles>();
            async Task<StorageFiles> OpenAsync(long storageMountId)
            {
                if (!connections.TryGetValue(storageMountId, out var files))
                {
                    var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(storageMountId, token), ct);
                    connections[storageMountId] = files = _storage.Open(mount);
                }

                return files;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.IsMissing)
                {
                    retired.Add(new RetiredFile(file.StoredFileId, file.NewMediaAssetId, null, null, null));
                    continue;
                }

                try
                {
                    var library = await OpenAsync(file.StorageMountId);
                    var source = StoragePath.Combine(file.RootPath, file.RelativePath);
                    var folder = await LibraryRecycleBin.FolderAsync(OpenAsync, settings, file.StorageMountId, file.RootPath, ct);
                    var target = settings.RecycleModeId == RecycleMode.OwnFolder ? source : file.RelativePath;
                    if (await LibraryRecycleBin.RecycleAsync(library, file.StorageMountId, source, folder, target, ct) is not { } recycled)
                    {
                        _logger.LogWarning("{Path} stays; it could not be moved or deleted.", file.RelativePath);
                    }
                    else
                    {
                        retired.Add(new RetiredFile(file.StoredFileId, file.NewMediaAssetId, folder?.StorageMountId, folder?.FolderPath, recycled.Length == 0 ? null : recycled));
                    }
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "{Path} stays; it could not be removed.", file.RelativePath);
                }
            }

            return retired;
        }

        private sealed record FileToRetire(long StoredFileId, long? NewMediaAssetId, long StorageMountId, string RootPath, string RelativePath, bool IsMissing);

        private sealed record RetiredFile(long StoredFileId, long? NewMediaAssetId, long? StorageMountId, string? FolderPath, string? RecycledPath);
    }
}
