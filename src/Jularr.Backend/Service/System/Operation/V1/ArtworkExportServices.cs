using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Operation.V1
{
    public sealed class ExecuteArtworkExportV1 : SystemService<OperationParametersV1, NoData>
    {
        private readonly ArtworkNasExporter _exporter;

        internal ExecuteArtworkExportV1(ArtworkNasExporter exporter)
        {
            _exporter = exporter;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(OperationParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var work = await Context.ExecuteBatchAsync((ctx, ct) =>
                ctx.ReadArtworkJobAsync(new ArtworkJobParameters(Parameters.OperationId), ct), CancellationToken);
            var result = await _exporter.ExportAsync(Context, work.Id, CancellationToken);

            var succeeded = await Context.ExecuteBatchAsync((ctx, ct) =>
                ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, result.SavedCount + result.SkippedCount,
                    OperationStatusType.Completed), ct), CancellationToken);
            return new UpdateResult(succeeded == 1);
        }
    }
}
