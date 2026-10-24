using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/storage/downloads/{downloadStorageAreaId:int}/cleanup")]
    public sealed class ReadDirectCleanupStatsV1 : AdminService<DownloadStorageParametersV1>
    {
        private readonly DirectDownloadCleanup _cleanup;

        internal ReadDirectCleanupStatsV1(DirectDownloadCleanup cleanup) => _cleanup = cleanup;

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DirectCleanupStatsV1>()];
        protected override ModuleRequirement GetInstanceModules(DownloadStorageParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadStorageParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override Task<IServiceOutput> OnRead()
        {
            if (Parameters.DownloadStorageAreaId != DownloadStorageArea.Direct)
                throw new DataValidationException(nameof(Parameters.DownloadStorageAreaId), "invalid_value");
            var stats = _cleanup.Stats;
            return Task.FromResult<IServiceOutput>(new DirectCleanupStatsV1(stats.Scanned, stats.Deleted, stats.Skipped, stats.Errors, stats.ReclaimedBytes));
        }
    }
}
