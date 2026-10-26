using System.Security;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    public sealed class ExecuteWorksSearchV1 : SystemService<OperationParametersV1, NoData>
    {
        private readonly WantedAcquisition _acquisition;
        private readonly SearchRunCancellation _cancellation;

        internal ExecuteWorksSearchV1(WantedAcquisition acquisition, SearchRunCancellation cancellation)
        {
            _acquisition = acquisition;
            _cancellation = cancellation;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationParametersV1 parameters) => ModuleRequirement.All("Library", "Requests", "Acquisition");
        protected override ServicePermission GetPermission(OperationParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var key = new SearchRunKeyData(Parameters.OperationId);
            var cancellation = _cancellation.Register(Parameters.OperationId, CancellationToken);
            try
            {
                var ct = cancellation.Token;
                while (await Context.ExecuteBatchAsync((ctx, token) => ctx.ReadNextSearchTargetAsync(key, token), ct) is { } item)
                {
                    // The original requester must still be enabled and allowed to manage Wanted.
                    if (!item.IsEnabled || !ServiceGate.GetPermissions(item.AccountId, null, null, item.AccountRoleTypeId).Contains("wanted.update"))
                    {
                        throw new SecurityException("The search requester is no longer allowed to manage Wanted.");
                    }

                    string outcome;
                    try
                    {
                        outcome = await _acquisition.SearchWholeTargetAsync(Context, item.WantedItemId, ct);
                    }
                    catch (DataValidationException exception)
                    {
                        outcome = exception.Code;
                    }
                    catch (ServiceUnavailableException exception)
                    {
                        outcome = exception.Code;
                    }
                    catch (KeyNotFoundException)
                    {
                        outcome = "not_found";
                    }
                    await Context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.RecordSearchTargetAsync(new SearchRunProgressData(key.OperationId, item.WantedItemId, outcome), token);
                        return true;
                    }, ct);
                }

                await Context.ExecuteBatchAsync((ctx, token) => ctx.CompleteWorksSearchAsync(key, token), ct);
            }
            catch (OperationCanceledException) when (!CancellationToken.IsCancellationRequested && cancellation.IsCancellationRequested)
            {
                // The Admin committed the final cancelled state before signalling this request.
            }
            finally
            {
                _cancellation.Remove(Parameters.OperationId);
            }

            return new UpdateResult(true);
        }
    }
}
