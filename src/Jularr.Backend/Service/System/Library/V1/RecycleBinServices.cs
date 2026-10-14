using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Library.V1;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.System.Library.V1
{
    public sealed class CleanRecycleBinsV1 : SystemService<CleanRecycleBinsParametersV1, NoData>
    {
        private readonly StorageOptions _storage;
        private readonly TimeProvider _time;

        internal CleanRecycleBinsV1(IOptions<StorageOptions> storage, TimeProvider time)
        {
            _storage = storage.Value;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CleanRecycleBinsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CleanRecycleBinsParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Every library's own recycle folder, also after a change of mode, and the configured folder; unreachable ones wait.
            var (settings, libraries) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await ctx.ReadReplacementSettingsAsync(ct), await ctx.ReadRecycleLibrariesAsync(ct)), CancellationToken);
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var library in libraries)
            {
                try
                {
                    var directory = StorageFileSystem.ResolveDirectory(_storage.GetConnectionPath(library.StorageProtocolTypeId, library.StorageMountId, library.SharePath), library.RootPath);
                    LibraryRecycleBin.Clean(Path.Combine(directory, StorageFileSystem.RecycleFolder), settings.RecycleDays, now);
                }
                catch (NasMountException)
                {
                }
            }

            if (!settings.RecyclesInLibraries)
            {
                try
                {
                    if (LibraryRecycleBin.Folder(_storage, settings, "") is { } folder)
                    {
                        LibraryRecycleBin.Clean(folder, settings.RecycleDays, now);
                    }
                }
                catch (NasMountException)
                {
                }
            }

            return new UpdateResult(true);
        }
    }
}
