using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Activity.V1
{
    // What a person does with a download of the queue; the client is told first, so a download that is still there is never forgotten.
    internal static class DownloadActions
    {
        // A removed download whose release stays allowed is searched again after this time, in the usual rhythm of an interrupted one.
        public static readonly TimeSpan RemovedRetry = TimeSpan.FromMinutes(30);

        public static SabnzbdConnection Connection(AcquisitionConnectionData connection, IDataProtector protector) =>
            new(connection.HttpAddress, AcquisitionConnectionInput.Unprotect(protector, connection.ProtectedApiKey));

        public static bool IsSabnzbd(DownloadActionData download) => download.AcquisitionConnectionTypeId == AcquisitionConnectionType.Sabnzbd;
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/remove")]
    public sealed class RemoveActivityDownloadV1 : AdminService<ActivityOperationParametersV1, RemoveActivityDownloadDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly DownloadStorage _downloads;
        private readonly TimeProvider _time;
        private readonly IDataProtector _protector;

        internal RemoveActivityDownloadV1(SabnzbdClient sabnzbd, DownloadStorage downloads, TimeProvider time, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _downloads = downloads;
            _time = time;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var download = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadActionAsync(Parameters.OperationId, ct), CancellationToken)
                ?? throw new KeyNotFoundException("The download is not in the queue.");

            // The client's files go with the job; its own downloader's folders are cleared once the operation is closed.
            if (DownloadActions.IsSabnzbd(download) && download.ExternalId is { } nzoId)
            {
                var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
                try
                {
                    await _sabnzbd.RemoveJobAsync(DownloadActions.Connection(connection, _protector), nzoId, true, CancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or SabnzbdException || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
                {
                    throw new ServiceUnavailableException("acquisition_unavailable");
                }
            }

            var nextSearchAt = Data.BlocksRelease ? _time.GetUtcNow().UtcDateTime : _time.GetUtcNow().UtcDateTime + DownloadActions.RemovedRetry;
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RemoveDownloadAsync(new DownloadRemovalData(Parameters.OperationId, Data.BlocksRelease, nextSearchAt), ct);
                return true;
            }, CancellationToken);

            if (!DownloadActions.IsSabnzbd(download))
            {
                await ClearOwnFoldersAsync(download);
            }

            return new UpdateResult(true);
        }

        // Best effort: what Jularr's own downloader wrote for the job.
        private async Task ClearOwnFoldersAsync(DownloadActionData download)
        {
            try
            {
                var area = download.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet ? DownloadStorageArea.Usenet : DownloadStorageArea.Direct;
                var paths = await _downloads.OpenAsync(Context, area, CancellationToken);
                foreach (var folder in new[] { paths.Incomplete(download.OperationId), paths.Complete(download.OperationId) })
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ServiceUnavailableException)
            {
            }
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/pause")]
    public sealed class PauseActivityDownloadV1 : AdminService<ActivityOperationParametersV1, PauseActivityDownloadDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly IDataProtector _protector;

        internal PauseActivityDownloadV1(SabnzbdClient sabnzbd, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var download = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadActionAsync(Parameters.OperationId, ct), CancellationToken)
                ?? throw new KeyNotFoundException("The download is not in the queue.");

            // Only SABnzbd can be told to wait.
            if (!DownloadActions.IsSabnzbd(download) || download.ExternalId is not { } nzoId || download.AttentionKey is not null)
            {
                throw new DataValidationException(nameof(PauseActivityDownloadDataV1.IsPaused), "invalid_transition");
            }

            var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
            try
            {
                await _sabnzbd.PauseJobAsync(DownloadActions.Connection(connection, _protector), nzoId, Data.IsPaused, CancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or SabnzbdException || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("acquisition_unavailable");
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SetDownloadPausedAsync(new DownloadPauseData(Parameters.OperationId, Data.IsPaused), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/retry-import")]
    public sealed class RetryActivityImportV1 : AdminService<ActivityOperationParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var retried = await Context.ExecuteBatchAsync((ctx, ct) => ctx.RetryDownloadImportAsync(new ActivityOperationData(Parameters.OperationId), ct), CancellationToken);
            if (!retried)
            {
                throw new DataValidationException(nameof(Parameters.OperationId), "invalid_transition");
            }

            return new UpdateResult(true);
        }
    }
}
