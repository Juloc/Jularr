using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Media;

namespace Jularr.Backend.Service.System.Transcoding.V1
{
    // Times a short real conversion through every encoder of this machine and keeps what it proposes.
    public sealed class ExecuteTranscodeBenchmarkV1 : SystemService<OperationParametersV1, NoData>
    {
        private readonly TranscodeBenchmarkClient _client;
        private readonly TranscodeHardware _hardware;

        internal ExecuteTranscodeBenchmarkV1(TranscodeBenchmarkClient client, TranscodeHardware hardware)
        {
            _client = client;
            _hardware = hardware;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(OperationParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!_client.IsAvailable)
            {
                throw new ServiceUnavailableException("benchmark_unavailable");
            }

            // Look at the machine again, then time every encoder it has.
            var hardware = await _hardware.GetAsync(CancellationToken, refresh: true);
            var results = await _client.MeasureAsync(hardware, CancellationToken);
            var cores = Environment.ProcessorCount;
            var proposal = TranscodeLogic.Propose(results, cores) ?? throw new ServiceUnavailableException("benchmark_failed");

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveTranscodeBenchmarkAsync(new TranscodeBenchmarkSaveData(cores, proposal.Acceleration, proposal.MaxTranscodes, TranscodeLogic.ResultsJson(results)), ct);
                return await ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, 1, OperationStatusType.Completed, null), ct);
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
