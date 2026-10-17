using System.Text.Json;
using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Admin.Artwork.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/artwork/export")]
    public sealed class ExportWorkArtworkV1 : AdminService<WorkParametersV1, NoData>
    {
        private readonly StorageAccess _storage;
        private readonly ArtworkImageDownloader _images;

        internal ExportWorkArtworkV1(StorageAccess storage, ArtworkImageDownloader images)
        {
            _storage = storage;
            _images = images;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ArtworkExportResultV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (settings, locations, facts) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadNasExportSettingsAsync(ct),
                    await ctx.ReadNasExportLocationsAsync(Parameters, ct),
                    await ctx.ReadNasExportFactsAsync(Parameters, ct)), CancellationToken);
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

                var target = new ArtworkExportFolderData(Parameters.WorkId, location.LibraryRootId, folder);
                var exclusive = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExclusiveNasFolderAsync(target, ct), CancellationToken);
                if (!exclusive.IsExclusive)
                {
                    continue;
                }

                var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(location.StorageMountId, ct), CancellationToken);
                if (mount.IsReadOnly)
                {
                    continue;
                }

                var files = _storage.Open(mount);
                var directory = StoragePath.Combine(location.RootRelativePath, folder);
                var entries = await files.ListAsync(directory, CancellationToken);
                var saved = 0;
                var skipped = 0;
                foreach (var (slot, sourceUrl) in images)
                {
                    if (entries.Any(item => !item.IsDirectory && ArtworkFileNames.Recognize(item.Name) is { } sidecar && sidecar.Slot == slot))
                    {
                        skipped++;
                        continue;
                    }

                    var jpeg = await _images.DownloadJpegAsync(sourceUrl, CancellationToken);
                    if (jpeg is null)
                    {
                        skipped++;
                        continue;
                    }

                    var name = ArtworkFileNames.ExportName(slot, jxl: false);
                    var relativePath = StoragePath.Combine(folder, name);
                    using var image = new MemoryStream(jpeg, writable: false);
                    if (await LibraryFileWriter.WriteAsync(files, location.RootRelativePath, relativePath, image, CancellationToken) is null)
                    {
                        skipped++;
                        continue;
                    }

                    var candidate = new ArtworkScanCandidate(relativePath, folder, (short)slot, null);
                    var batch = new ArtworkScanBatchData(location.LibraryRootId, StoredFileLogic.StoredTime(DateTime.UtcNow),
                        JsonSerializer.Serialize(new[] { candidate }));
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.UpsertSidecarsAsync(batch, ct);
                        return true;
                    }, CancellationToken);
                    saved++;
                }

                return new ArtworkExportResultV1(saved, skipped);
            }

            return new ArtworkExportResultV1(0, images.Count);
        }
    }
}
