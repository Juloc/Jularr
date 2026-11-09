using System.IO.Compression;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Fetches the covers still named by a provider (titles that existed before covers were kept, a provider that was down) and the season and episode images; true while more are waiting.
    public sealed class StoreCoversV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int PassSize = 20;
        private readonly CoverStore _covers;

        internal StoreCoversV1(CoverStore covers)
        {
            _covers = covers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!_covers.IsEnabled)
            {
                return new UpdateResult(false);
            }

            var targets = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadPendingCoversAsync(new WorkCoverQueryData(null, PassSize), ct), CancellationToken);
            if (targets.Count == 0)
            {
                return new UpdateResult(false);
            }

            await _covers.StoreAsync(Context, targets, CancellationToken);
            return new UpdateResult(targets.Count == PassSize);
        }
    }

    // Gives the books of the library that no provider knows a cover from their own file: the cover of an EPUB, the first page of a comic archive. A few books per pass; true while more are
    // waiting. A book without one, or whose storage is offline, rests a week; a provider that names a poster for it later replaces this one.
    public sealed class StoreBookFileCoversV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int PassSize = 5;
        private readonly CoverStore _covers;
        private readonly StorageAccess _storage;

        internal StoreBookFileCoversV1(CoverStore covers, StorageAccess storage)
        {
            _covers = covers;
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!_covers.IsEnabled)
            {
                return new UpdateResult(false);
            }

            var books = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadFileCoverTargetsAsync(new WorkCoverQueryData(null, PassSize), ct), CancellationToken);
            foreach (var book in books)
            {
                (byte[] Bytes, string ContentType)? cover = null;
                try
                {
                    cover = await ReadCoverAsync(book);
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException or InvalidDataException or ServiceUnavailableException)
                {
                    // A file that cannot be read now is tried again in a week.
                }

                if (cover is { } found)
                {
                    await _covers.StoreFileAsync(Context, book.WorkId, found.Bytes, found.ContentType, CancellationToken);
                }
                else
                {
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.SaveWorkCoverFailureAsync(new WorkCoverTargetData(book.WorkId, CoverKind.Poster, null, CoverStore.FileSource), ct);
                        return true;
                    }, CancellationToken);
                }
            }

            return new UpdateResult(books.Count == PassSize);
        }

        private async Task<(byte[] Bytes, string ContentType)?> ReadCoverAsync(ReadingSourceData book)
        {
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(book.StorageMountId, ct), CancellationToken);
            await using var file = await _storage.Open(mount).OpenReadAsync(StoragePath.Combine(book.RootRelativePath, book.RelativePath), CancellationToken);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            return book.RelativePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) ? BookCoverLogic.FromEpub(archive) : BookCoverLogic.FromComic(archive);
        }
    }

    // Brings the widths of stored covers, then of the cached images of other titles, into the one format the Owner chose; true while more are waiting. A pass is a few images, paced by its worker.
    public sealed class ConvertCoversV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int PassSize = 5;
        private readonly CoverStore _covers;

        internal ConvertCoversV1(CoverStore covers)
        {
            _covers = covers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Without a folder or ffmpeg there is nothing to make widths with.
            if (!_covers.IsEnabled || !_covers.CanScale)
            {
                return new UpdateResult(false);
            }

            var format = _covers.EffectiveFormat((await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadCoverCacheSettingsAsync(ct), CancellationToken)).FormatId);
            var covers = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadCoversToConvertAsync(new WorkCoverConvertQueryData(format, PassSize), ct), CancellationToken);
            foreach (var cover in covers)
            {
                // A cover that cannot be converted keeps the files it has; it is not tried again until the format changes.
                await _covers.ConvertAsync(cover, format, CancellationToken);
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.SaveWorkCoverFormatAsync(new WorkCoverFormatData(cover, format), ct);
                    return true;
                }, CancellationToken);
            }

            if (covers.Count == PassSize)
            {
                return new UpdateResult(true);
            }

            return new UpdateResult(await _covers.ConvertExternalAsync(format, PassSize - covers.Count, CancellationToken) > 0);
        }
    }

    // Removes cover files no stored cover refers to, such as those of deleted Works, and the cached images of titles that are not Works nobody uses any more.
    public sealed class CleanCoversV1 : SystemService<CoverParametersV1, NoData>
    {
        private readonly CoverStore _covers;

        internal CleanCoversV1(CoverStore covers)
        {
            _covers = covers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The cache of titles that are not Works keeps to the Owner's days and size.
            var settings = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadCoverCacheSettingsAsync(ct), CancellationToken);
            var trimmed = _covers.TrimExternal(settings.KeepDays, settings.MaximumMegabytes * 1024L * 1024L);
            return new UpdateResult(await _covers.SweepAsync(Context, CancellationToken) + trimmed > 0);
        }
    }
}
