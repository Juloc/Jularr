using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Qbittorrent;
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
        public static bool IsQbittorrent(DownloadActionData download) => download.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent;
        public static QbittorrentConnection TorrentConnection(AcquisitionConnectionData connection, IDataProtector protector) =>
            new(connection.HttpAddress, connection.Username, AcquisitionConnectionInput.Unprotect(protector, connection.ProtectedPassword));

        // Jularr's own downloaders wait when told to: their jobs are simply not run while paused.
        public static bool IsNative(DownloadActionData download) => download.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet || DirectSources.IsDirect(download.AcquisitionConnectionTypeId);

        // Tells the client, or marks the own downloader's job, to wait or to go on; false when the download cannot be paused.
        public static async Task<bool> SetPausedAsync(LogicContext context, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, IDataProtector protector, DownloadActionData download, bool isPaused, CancellationToken ct)
        {
            var usable = download.AttentionKey is null && ((IsSabnzbd(download) || IsQbittorrent(download)) && download.ExternalId is not null || IsNative(download));
            if (!usable)
            {
                return false;
            }

            if (IsSabnzbd(download))
            {
                var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, token), ct);
                try
                {
                    await sabnzbd.PauseJobAsync(Connection(connection, protector), download.ExternalId!, isPaused, ct);
                }
                catch (Exception exception) when (exception is HttpRequestException or SabnzbdException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new ServiceUnavailableException("acquisition_unavailable");
                }
            }

            if (IsQbittorrent(download))
            {
                var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, token), ct);
                try
                {
                    await qbittorrent.SetPausedAsync(TorrentConnection(connection, protector), download.ExternalId!, isPaused, ct);
                }
                catch (Exception exception) when (exception is HttpRequestException or QbittorrentException
                    || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new ServiceUnavailableException("acquisition_unavailable");
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SetDownloadPausedAsync(new DownloadPauseData(download.OperationId, isPaused), token);
                return true;
            }, ct);
            return true;
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/remove")]
    public sealed class RemoveActivityDownloadV1 : AdminService<ActivityOperationParametersV1, RemoveActivityDownloadDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly DownloadStorage _downloads;
        private readonly TimeProvider _time;
        private readonly IDataProtector _protector;

        internal RemoveActivityDownloadV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadStorage downloads, TimeProvider time, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
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

            if (DownloadActions.IsQbittorrent(download) && download.ExternalId is { } torrentHash)
            {
                var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
                try
                {
                    // Explicit removal in Activity deletes the job and its torrent payload.
                    await _qbittorrent.RemoveAsync(DownloadActions.TorrentConnection(connection, _protector), torrentHash, true, CancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or QbittorrentException
                    || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
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

            if (DownloadActions.IsNative(download))
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
        private readonly QbittorrentClient _qbittorrent;
        private readonly IDataProtector _protector;

        internal PauseActivityDownloadV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
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

            // SABnzbd is told to wait, Jularr's own downloaders skip the job.
            if (!await DownloadActions.SetPausedAsync(Context, _sabnzbd, _qbittorrent, _protector, download, Data.IsPaused, CancellationToken))
            {
                throw new DataValidationException(nameof(PauseActivityDownloadDataV1.IsPaused), "invalid_transition");
            }

            return new UpdateResult(true);
        }
    }

    // The whole queue waits or goes on: every running download that can be paused, each as if it had been chosen on its own.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/queue/pause")]
    public sealed class PauseActivityQueueV1 : AdminService<PauseActivityQueueParametersV1, PauseActivityDownloadDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly IDataProtector _protector;

        internal PauseActivityQueueV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PauseActivityQueueParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(PauseActivityQueueParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var ids = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadRunningDownloadIdsAsync(ct), CancellationToken);
            foreach (var id in ids)
            {
                if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadActionAsync(id, ct), CancellationToken) is { } download)
                {
                    await DownloadActions.SetPausedAsync(Context, _sabnzbd, _qbittorrent, _protector, download, Data.IsPaused, CancellationToken);
                }
            }

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
