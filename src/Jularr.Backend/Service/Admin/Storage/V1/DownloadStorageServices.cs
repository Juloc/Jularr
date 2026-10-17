using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/storage/downloads")]
    public sealed class ReadDownloadStorageV1 : AdminService<ReadDownloadStorageParametersV1>
    {
        private readonly StorageOptions _options;

        internal ReadDownloadStorageV1(IOptions<StorageOptions> options)
        {
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DownloadStorageDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadDownloadStorageParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReadDownloadStorageParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override Task<IServiceOutput> OnRead() => Task.FromResult<IServiceOutput>(new DownloadStorageDetailsV1(
            _options.UsenetDownloadPath ?? _options.DirectDownloadPath, _options.DirectSourceDownloadPath ?? _options.DirectDownloadPath,
            _options.UsenetMaxJobBytes, _options.AllowDirectDownloadOnHostFilesystem));
    }
}
