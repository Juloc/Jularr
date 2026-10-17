using System.Text.Json;
using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Shared, bounded NAS work for both the explicit Owner action and System's operation queue.
    internal sealed class ArtworkNasExporter(StorageAccess storage, ArtworkImageDownloader images)
    {
        private readonly StorageAccess _storage = storage;
        private readonly ArtworkImageDownloader _images = images;

        public async Task<ArtworkExportResultV1> ExportAsync(LogicContext context, long workId, CancellationToken ct)
        {
            var (settings, locations, facts) = await context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadNasExportSettingsAsync(ct),
                    await ctx.ReadNasExportLocationsAsync(new WorkParametersV1(workId), ct),
                    await ctx.ReadNasExportFactsAsync(new WorkParametersV1(workId), ct)), ct);
            if (!settings.ExportToNas)
            {
                return new ArtworkExportResultV1(0, 0);
            }

            if (settings.NasModeId != NasArtworkMode.Compatible)
            {
                // Never silently produce only JPEG when the configured mode promises JPEG XL.
                throw new ServiceUnavailableException("artwork_jxl_not_ready");
            }

            var images = facts
                .Where(item => item.FactKey is "Poster" or "Backdrop")
                .GroupBy(item => item.FactKey)
                .Select(group => group.First())
                .ToDictionary(item => item.FactKey == "Poster" ? ArtworkSlot.Poster : ArtworkSlot.Backdrop, item => item.Value);
            if (images.Count == 0)
            {
                return new ArtworkExportResultV1(0, 0);
            }

            // Only export into a folder that belongs unambiguously to this Work.
            foreach (var location in locations)
            {
                var folder = ArtworkExportDirectory.For(location.MediaTypeId, location.RelativePath);
                if (folder is null)
                {
                    continue;
                }

                var target = new ArtworkExportFolderData(workId, location.LibraryRootId, folder);
                var exclusive = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExclusiveNasFolderAsync(target, ct), ct);
                if (!exclusive.IsExclusive)
                {
                    continue;
                }

                var mount = await context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(location.StorageMountId, ct), ct);
                if (mount.IsReadOnly)
                {
                    continue;
                }

                var files = _storage.Open(mount);
                var directory = StoragePath.Combine(location.RootRelativePath, folder);
                var entries = await files.ListAsync(directory, ct);
                var saved = 0;
                var skipped = 0;
                foreach (var (slot, sourceUrl) in images)
                {
                    if (entries.Any(item => !item.IsDirectory && ArtworkFileNames.Recognize(item.Name) is { } sidecar && sidecar.Slot == slot))
                    {
                        skipped++;
                        continue;
                    }

                    var jpeg = await _images.DownloadJpegAsync(sourceUrl, ct);
                    if (jpeg is null)
                    {
                        skipped++;
                        continue;
                    }

                    var name = ArtworkFileNames.ExportName(slot, jxl: false);
                    var relativePath = StoragePath.Combine(folder, name);
                    using var image = new MemoryStream(jpeg, writable: false);
                    if (await LibraryFileWriter.WriteAsync(files, location.RootRelativePath, relativePath, image, ct) is null)
                    {
                        skipped++;
                        continue;
                    }

                    var candidate = new ArtworkScanCandidate(relativePath, folder, (short)slot, null);
                    var batch = new ArtworkScanBatchData(location.LibraryRootId, StoredFileLogic.StoredTime(DateTime.UtcNow),
                        JsonSerializer.Serialize(new[] { candidate }));
                    await context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.UpsertSidecarsAsync(batch, ct);
                        return true;
                    }, ct);
                    saved++;
                }

                return new ArtworkExportResultV1(saved, skipped);
            }

            return new ArtworkExportResultV1(0, images.Count);
        }
    }
}
