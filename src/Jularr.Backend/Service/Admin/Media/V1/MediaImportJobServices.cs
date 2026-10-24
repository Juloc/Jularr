using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Stored as an immutable, server-validated snapshot. Once queued, editing
    // a preview cannot alter the choices of an already running job.
    public sealed class QueueMediaImportJobV1 : AdminService<MediaImportParametersV1, MediaImportDataV1>
    {
        private readonly WorkSignals _signals;

        internal QueueMediaImportJobV1(WorkSignals signals) => _signals = signals;

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaImportJobCreatedV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaImportParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(MediaImportParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var json = await MediaImportText.ReadAsync(Data, CancellationToken);
            var items = MediaImportParser.Parse(json);
            var choices = Data.Choices.ToArray();
            if (choices.Any(choice => choice.Index < 0 || choice.Index >= items.Count))
                throw new DataValidationException(nameof(Data.Choices), "out_of_range");
            // Durable imports never infer consent for unposted rows. The preview
            // emits an Include value for every row, including disabled ones.
            if (!choices.Any(choice => choice.Include == true))
                throw new DataValidationException(nameof(Data.Choices), "required");
            var payload = new MediaImportJobPayload(MediaImportParser.Write(items), Data.Action, Data.Defaults.ToArray(), choices);
            var encoded = JsonSerializer.Serialize(payload);

            // Admission and owner identity are verified by the standard AdminService
            // gate, including transactional account/session and module checks.
            var id = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var jobId = await ctx.QueueMediaImportJobAsync(
                    new MediaImportJobQueueData(Auth.AccountId!.Value, Guid.NewGuid(), encoded, items.Count), ct);
                await ctx.InitializeImportJobRowsAsync(new MediaImportJobIdData(jobId), ct);
                return jobId;
            }, CancellationToken);
            _signals.Imports.Notify();
            return new MediaImportJobCreatedV1(id);
        }
    }

    // REST API uses a JSON-only contract: multipart UploadedFile forms cannot
    // bind a nested BaseDataList through FormDataBinder. The shared enqueue service
    // remains the only implementation of admission and operation scheduling.
    [ServiceEndpoint("POST", "/api/v1/admin/media-import/jobs")]
    public sealed class QueueMediaImportJobApiV1 : AdminService<MediaImportParametersV1, MediaImportJobRequestV1>
    {
        private readonly QueueMediaImportJobV1 _queue;
        internal QueueMediaImportJobApiV1(QueueMediaImportJobV1 queue) => _queue = queue;

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaImportJobCreatedV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaImportParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(MediaImportParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute() =>
            await _queue.ExecuteAsync<MediaImportJobCreatedV1>(
                new MediaImportDataV1(null, Data.Json, Data.Action, Data.Defaults, Data.Choices), CancellationToken);
    }

    [ServiceEndpoint("GET", "/api/v1/admin/media-import/jobs/{jobId:guid}")]
    public sealed class ReadMediaImportJobV1 : AdminService<MediaImportJobParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaImportJobStatusV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaImportJobParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(MediaImportJobParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnRead() =>
            await Context.ReadImportJobStatusAsync(new MediaImportJobKeyData(Parameters.JobId, Auth.AccountId!.Value), CancellationToken);
    }

    [ServiceEndpoint("GET", "/api/v1/admin/media-import/jobs/latest")]
    public sealed class LatestMediaImportJobV1 : AdminService<MediaImportParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaImportLatestJobV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaImportParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(MediaImportParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnRead()
        {
            var latest = await Context.ReadLatestImportJobAsync(new MediaImportJobOwnerData(Auth.AccountId!.Value), CancellationToken);
            return new MediaImportLatestJobV1(latest?.JobId);
        }
    }
}
