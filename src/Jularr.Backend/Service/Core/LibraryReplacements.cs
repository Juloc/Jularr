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

        // A file that cannot be moved, on an unreachable library or with an unusable recycle folder, stays as a further version. The history names each file that went.
        private async Task RetireAsync(LogicContext context, ReplacementSettingsData settings, IReadOnlyList<ReplacedFileData> files, ActivityEventType eventType, long? accountId,
            CancellationToken ct)
        {
            var retired = new List<object>();
            var gone = new List<long>();
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

            foreach (var file in files.DistinctBy(file => file.StoredFileId))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var library = await OpenAsync(file.StorageMountId);
                    var source = StoragePath.Combine(file.RootPath, file.RelativePath);
                    var folder = await LibraryRecycleBin.FolderAsync(OpenAsync, settings, file, ct);
                    var target = settings.RecycleModeId == RecycleMode.OwnFolder ? source : file.RelativePath;
                    if (await LibraryRecycleBin.RecycleAsync(library, file.StorageMountId, source, folder, target, ct) is not { } recycled)
                    {
                        _logger.LogWarning("{Path} stays as a further version; it could not be moved or deleted.", file.RelativePath);
                    }
                    else
                    {
                        gone.Add(file.StoredFileId);
                        retired.Add(new
                        {
                            file.StoredFileId,
                            file.NewMediaAssetId,
                            folder?.StorageMountId,
                            folder?.FolderPath,
                            RecycledPath = recycled.Length == 0 ? null : recycled
                        });
                    }
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "{Path} stays as a further version; it could not be replaced.", file.RelativePath);
                }
            }

            if (retired.Count > 0)
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    foreach (var storedFileId in gone)
                    {
                        await ctx.RecordActivityAsync(new ActivityEventData(eventType, storedFileId: storedFileId, accountId: accountId), token);
                    }

                    await ctx.RetireReplacedFilesAsync(new RetiredFilesData(JsonSerializer.Serialize(retired)), token);
                    return true;
                }, ct);
            }
        }
    }
}
