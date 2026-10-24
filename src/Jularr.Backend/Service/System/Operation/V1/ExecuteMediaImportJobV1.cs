using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Operation.V1
{
    // The existing OperationWorker owns this execution, not the browser request.
    // Work is processed and journaled one title at a time; a restart resumes
    // at the next uncommitted row and rechecks the canonical Work identity.
    public sealed class ExecuteMediaImportJobV1 : SystemService<OperationParametersV1, NoData>
    {
        private readonly MediaImportPlanner _planner;
        private readonly MediaImportProcessor _processor;
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _searches;

        internal ExecuteMediaImportJobV1(MediaImportPlanner planner, MediaImportProcessor processor, AcquisitionSearchSignal searchSignal)
        {
            _planner = planner;
            _processor = processor;
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(OperationParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var job = await Context.ReadImportJobWorkAsync(new MediaImportJobOperationData(Parameters.OperationId), CancellationToken)
                ?? throw new KeyNotFoundException("The running media import job was not found.");
            var stored = JsonSerializer.Deserialize<MediaImportJobPayload>(job.Payload)
                ?? throw new InvalidOperationException("The stored media import is invalid.");
            var items = MediaImportParser.Parse(stored.Json);
            if (items.Count != job.TotalCount || job.Cursor > items.Count)
            {
                throw new InvalidOperationException("The stored import list no longer matches its checkpoint.");
            }

            // Every canonical short DB write batch rechecks and row-locks the
            // requesting account and required modules, including after provider I/O.
            Context.AddTransactionalGuard(async (ctx, ct) =>
            {
                if (!(await ctx.CheckImportJobOwnerAsync(new MediaImportJobOwnerData(job.RequestedByAccountId), ct)).Allowed)
                    throw new UnauthorizedAccessException("The requesting administrator lost import permission.");
            });

            async Task CheckOwnerAsync()
            {
                var permission = await Context.ExecuteBatchAsync((ctx, ct) =>
                    ctx.CheckImportJobOwnerAsync(new MediaImportJobOwnerData(job.RequestedByAccountId), ct), CancellationToken);
                if (!permission.Allowed)
                {
                    throw new UnauthorizedAccessException("The import owner is no longer enabled or authorized.");
                }
            }

            for (var index = job.Cursor; index < items.Count; index++)
            {
                CancellationToken.ThrowIfCancellationRequested();
                await CheckOwnerAsync();

                // This is the same canonical planner/processor as the old HTTP import.
                // A single-item list makes every restart and re-evaluation idempotent.
                var json = MediaImportParser.Write([items[index]]);
                var selected = stored.Choices.FirstOrDefault(choice => choice.Index == index);
                // Never interpret a missing or nullable choice as automatic inclusion.
                var choices = new[]
                {
                    selected is null ? new MediaImportChoiceV1(0, false, null, null, null, null, null)
                        : new MediaImportChoiceV1(0, selected.Include == true, selected.Choice, selected.Query,
                            selected.Action, selected.AcquisitionProfileId, selected.LibraryRootId)
                };
                var data = new MediaImportDataV1(null, json, stored.Action, new(stored.Defaults), new(choices));
                var plan = await _planner.PlanAsync(Context, MediaImportParser.Parse(json), data, 0, CancellationToken);
                var row = plan.Rows.Single();

                // A temporarily unavailable provider is tried a bounded number of times.
                // An unresolved row remains explicitly visible in the outcome journal.
                MediaImportRowExecution processed;
                for (var attempt = 0; ; attempt++)
                {
                    processed = await _processor.ProcessAsync(Context, row, CancellationToken, CheckOwnerAsync);
                    if (processed.Result.OutcomeTypeId != MediaImportOutcomeType.Unavailable || attempt >= 2) break;
                    await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 5), CancellationToken);
                    await CheckOwnerAsync();
                }

                var result = processed.Result;
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    // Guard the journal step against permission or module revocation too.
                    if (!(await ctx.CheckImportJobOwnerAsync(new MediaImportJobOwnerData(job.RequestedByAccountId), ct)).Allowed)
                        throw new UnauthorizedAccessException("The import owner lost permission.");
                    await ctx.RecordImportJobRowAsync(new MediaImportJobCheckpointData(job.OperationId, index, result.OutcomeTypeId,
                        result.Title, result.WorkId, result.Searches), ct);
                    return true;
                }, CancellationToken);
                _searches |= result.Searches;

                // Prevent thousands of provider imports from monopolizing CPU or API quotas.
                if ((index + 1) % 40 == 0) await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                if (!(await ctx.CheckImportJobOwnerAsync(new MediaImportJobOwnerData(job.RequestedByAccountId), ct)).Allowed)
                    throw new UnauthorizedAccessException("The import owner lost permission.");
                return await ctx.UpdateOperationAsync(new OperationProgressData(job.OperationId, items.Count, OperationStatusType.Completed), ct);
            }, CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted()
        {
            if (_searches) _searchSignal.RequestSearch();
        }
    }
}
