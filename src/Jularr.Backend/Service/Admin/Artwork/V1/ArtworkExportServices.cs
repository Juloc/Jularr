using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Artwork.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/artwork/export")]
    public sealed class ExportWorkArtworkV1 : AdminService<WorkParametersV1, NoData>
    {
        private readonly ArtworkNasExporter _exporter;

        internal ExportWorkArtworkV1(ArtworkNasExporter exporter)
        {
            _exporter = exporter;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ArtworkExportResultV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute() =>
            await _exporter.ExportAsync(Context, Parameters.WorkId, CancellationToken);
    }
}
