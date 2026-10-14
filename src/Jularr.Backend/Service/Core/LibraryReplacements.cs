using System.Text.Json;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // After an import: the earlier files of the same film, episode, track or volume leave the library, unless several releases are kept.
    internal sealed class LibraryReplacements
    {
        private readonly StorageOptions _storage;

        public LibraryReplacements(IOptions<StorageOptions> storage)
        {
            _storage = storage.Value;
        }

        public async Task ReplaceAsync(LogicContext context, IReadOnlyList<long> newMediaAssetIds, CancellationToken ct)
        {
            if (newMediaAssetIds.Count == 0)
            {
                return;
            }

            var (settings, files) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadReplacementSettingsAsync(token), await ctx.ReadReplacedFilesAsync(new ReplacingAssetsData([.. newMediaAssetIds]), token)), ct);
            if (settings.KeepsReleases || files.Count == 0)
            {
                return;
            }

            // A file that cannot be moved, on an unreachable library or with an unusable recycle folder, stays as a further version.
            var retired = new List<object>();
            foreach (var file in files.DistinctBy(file => file.StoredFileId))
            {
                try
                {
                    var library = StorageFileSystem.ResolveDirectory(_storage.GetConnectionPath(file.StorageProtocolTypeId, file.StorageMountId, file.SharePath), file.RootPath);
                    var target = settings.RecyclesInLibraries || file.RootPath.Length == 0 ? file.RelativePath : file.RootPath + "/" + file.RelativePath;
                    var source = StorageFileSystem.ResolveFile(library, file.RelativePath);
                    var folder = LibraryRecycleBin.Folder(_storage, settings, library);
                    if (await Task.Run(() => LibraryRecycleBin.Recycle(source, folder, target), ct))
                    {
                        retired.Add(new { file.StoredFileId, file.NewMediaAssetId });
                    }
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                {
                }
            }

            if (retired.Count > 0)
            {
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.RetireReplacedFilesAsync(new RetiredFilesData(JsonSerializer.Serialize(retired)), token);
                    return true;
                }, ct);
            }
        }
    }
}
