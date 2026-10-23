using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.System.Library.V1;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.System.Library.V1
{
    public sealed class CleanRecycleBinsV1 : SystemService<CleanRecycleBinsParametersV1, NoData>
    {
        private readonly StorageAccess _storage;

        internal CleanRecycleBinsV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CleanRecycleBinsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CleanRecycleBinsParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only files Jularr recycled and registered leave, never anything else in those folders; unreachable ones wait.
            var expired = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExpiredRecycledFilesAsync(ct), CancellationToken);
            var gone = new List<long>();
            foreach (var file in expired)
            {
                try
                {
                    var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(file.StorageMountId, ct), CancellationToken);
                    if (await LibraryRecycleBin.DeleteAsync(_storage.Open(mount), file.FolderPath, file.RelativePath, CancellationToken))
                    {
                        gone.Add(file.Id);
                    }
                }
                catch (Exception exception) when (exception is NasMountException or KeyNotFoundException)
                {
                    // Cleaned on a later pass.
                }
            }

            if (gone.Count > 0)
            {
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.ForgetRecycledFilesAsync(new RecycledFileIdsData([.. gone]), ct);
                    return true;
                }, CancellationToken);
            }

            return new UpdateResult(true);
        }
    }
}
