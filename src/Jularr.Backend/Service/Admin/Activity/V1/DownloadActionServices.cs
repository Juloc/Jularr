using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Storage;
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
        public static bool IsNative(DownloadActionData download) => download.AcquisitionConnectionTypeId is AcquisitionConnectionType.Usenet or AcquisitionConnectionType.Torrent
            || DirectSources.IsDirect(download.AcquisitionConnectionTypeId);

        // A client that does not answer keeps everything as it was.
        public static bool IsUnavailable(Exception exception, CancellationToken ct) => exception is HttpRequestException or SabnzbdException or QbittorrentException or global::System.Text.Json.JsonException
            || exception is TaskCanceledException && !ct.IsCancellationRequested;

        // The part of the queue a download belongs to: its client, by kind, and for SABnzbd and qBittorrent its connection.
        public static DownloadQueueScopeData ScopeOf(DownloadActionData download, DownloadClientKind kind) =>
            new(kind, kind is DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent ? download.AcquisitionConnectionId : null);

        // SABnzbd's priority of a level: -1 low, 0 normal, 1 high, 2 force.
        public static int SabnzbdPriority(DownloadPriorityType priority) => (int)priority - 2;

        // Moves a download first in its priority, before another download (taking that one's priority), or to the end; SABnzbd and qBittorrent are told first.
        public static async Task<bool> MoveAsync(LogicContext context, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, IDataProtector protector, DownloadActionData download,
            long? beforeOperationId, bool toTop, CancellationToken ct)
        {
            if (DownloadClientKinds.Of(download.AcquisitionConnectionTypeId) is not { } kind || !toTop && !DownloadClientKinds.Reorders(kind))
            {
                return false;
            }

            // The new order: the download leaves its place and goes before the first of its priority, before the chosen one, or last.
            var queue = (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDownloadQueueAsync(ScopeOf(download, kind), token), ct)).ToList();
            var moving = queue.FirstOrDefault(entry => entry.OperationId == download.OperationId);
            if (moving is null) return false;
            queue.Remove(moving);
            var before = toTop ? queue.FirstOrDefault(entry => entry.DownloadPriorityTypeId <= moving.DownloadPriorityTypeId)
                : beforeOperationId is { } id ? queue.FirstOrDefault(entry => entry.OperationId == id) : null;
            if (!toTop && beforeOperationId is not null && before is null) return false;
            var priority = toTop || before is null ? moving.DownloadPriorityTypeId : before.DownloadPriorityTypeId;
            queue.Insert(before is null ? (toTop ? 0 : queue.Count) : queue.IndexOf(before), new DownloadQueueEntryData(moving.OperationId, priority, moving.QueuePosition));

            // The client hears it first: SABnzbd takes the priority and the place of the job it now stands before; qBittorrent only puts a torrent first.
            if (download.ExternalId is { } externalId && (DownloadActions.IsSabnzbd(download) || DownloadActions.IsQbittorrent(download)))
            {
                var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, token), ct);
                try
                {
                    if (DownloadActions.IsQbittorrent(download))
                    {
                        await qbittorrent.MoveToTopAsync(TorrentConnection(connection, protector), externalId, ct);
                    }
                    else
                    {
                        if (priority != moving.DownloadPriorityTypeId) await sabnzbd.SetPriorityAsync(Connection(connection, protector), externalId, SabnzbdPriority(priority), ct);
                        var target = toTop ? 0 : before?.QueuePosition ?? queue.Count - 1;
                        await sabnzbd.MoveAsync(Connection(connection, protector), externalId, target, ct);
                    }
                }
                catch (Exception exception) when (IsUnavailable(exception, ct))
                {
                    throw new ServiceUnavailableException("acquisition_unavailable");
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                if (priority != moving.DownloadPriorityTypeId) await ctx.SetDownloadPriorityAsync(new DownloadPriorityData([moving.OperationId], priority), token);
                await ctx.SaveDownloadQueueOrderAsync(new DownloadQueueOrderData([.. queue.Select(entry => entry.OperationId)]), token);
                return true;
            }, ct);
            return true;
        }

        // A priority for one download: SABnzbd hears it first, Jularr's own downloaders read it with the queue; qBittorrent has none.
        public static async Task<bool> PrioritizeAsync(LogicContext context, SabnzbdClient sabnzbd, IDataProtector protector, DownloadActionData download, DownloadPriorityType priority, CancellationToken ct)
        {
            if (DownloadClientKinds.Of(download.AcquisitionConnectionTypeId) is not { } kind || !DownloadClientKinds.HasPriorities(kind))
            {
                return false;
            }

            if (IsSabnzbd(download) && download.ExternalId is { } nzoId)
            {
                var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, token), ct);
                try
                {
                    await sabnzbd.SetPriorityAsync(Connection(connection, protector), nzoId, SabnzbdPriority(priority), ct);
                }
                catch (Exception exception) when (IsUnavailable(exception, ct))
                {
                    throw new ServiceUnavailableException("acquisition_unavailable");
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.SetDownloadPriorityAsync(new DownloadPriorityData([download.OperationId], priority), token);
                return true;
            }, ct);
            return true;
        }

        // Takes a download out: the client's job and files go first, then the operation closes and its target is searched again (at once when the release is blocked); Jularr's own folders are cleared last.
        public static async Task RemoveAsync(LogicContext context, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadStorage downloads, IDataProtector protector, DateTime now,
            DownloadActionData download, bool blocksRelease, CancellationToken ct)
        {
            if (IsSabnzbd(download) && download.ExternalId is { } nzoId)
            {
                var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, token), ct);
                try
                {
                    await sabnzbd.RemoveJobAsync(Connection(connection, protector), nzoId, true, ct);
                }
                catch (Exception exception) when (exception is HttpRequestException or SabnzbdException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new ServiceUnavailableException("acquisition_unavailable");
                }
            }

            if (IsQbittorrent(download) && download.ExternalId is { } torrentHash)
            {
                var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, token), ct);
                try
                {
                    // An explicit removal deletes the job and its torrent payload.
                    await qbittorrent.RemoveAsync(TorrentConnection(connection, protector), torrentHash, true, ct);
                }
                catch (Exception exception) when (exception is HttpRequestException or QbittorrentException
                    || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new ServiceUnavailableException("acquisition_unavailable");
                }
            }

            var nextSearchAt = blocksRelease ? now : now + RemovedRetry;
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.RemoveDownloadAsync(new DownloadRemovalData(download.OperationId, blocksRelease, nextSearchAt), token);
                return true;
            }, ct);

            if (IsNative(download))
            {
                await ClearOwnFoldersAsync(context, downloads, download, ct);
            }
        }

        // Best effort: what Jularr's own downloader wrote for the job.
        private static async Task ClearOwnFoldersAsync(LogicContext context, DownloadStorage downloads, DownloadActionData download, CancellationToken ct)
        {
            try
            {
                var area = download.AcquisitionConnectionTypeId switch
                {
                    AcquisitionConnectionType.Usenet => DownloadStorageArea.Usenet,
                    AcquisitionConnectionType.Torrent => DownloadStorageArea.Torrent,
                    _ => DownloadStorageArea.Direct
                };
                var paths = await downloads.OpenAsync(context, area, ct);
                paths.RemoveCompleted(download.OperationId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ServiceUnavailableException)
            {
            }
        }

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
            await DownloadActions.RemoveAsync(Context, _sabnzbd, _qbittorrent, _downloads, _protector, _time.GetUtcNow().UtcDateTime, download, Data.BlocksRelease, CancellationToken);
            return new UpdateResult(true);
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
            // One client's part of the queue when the parameters name it; a forced download keeps running.
            var ids = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadRunningDownloadIdsAsync(ct), CancellationToken);
            foreach (var id in ids)
            {
                if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadActionAsync(id, ct), CancellationToken) is { } download
                    && (Parameters.DownloadClientKindId is not { } kind || DownloadClientKinds.Of(download.AcquisitionConnectionTypeId) == kind)
                    && (Parameters.AcquisitionConnectionId is not { } connectionId || download.AcquisitionConnectionId == connectionId)
                    && !(Data.IsPaused && download.DownloadPriorityTypeId == DownloadPriorityType.Force))
                {
                    await DownloadActions.SetPausedAsync(Context, _sabnzbd, _qbittorrent, _protector, download, Data.IsPaused, CancellationToken);
                }
            }

            return new UpdateResult(true);
        }
    }

    // The chosen downloads of the queue, each as if it had been chosen on its own; a download its client cannot do this for, or that has left the queue, is skipped.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/downloads")]
    public sealed class ApplyActivityDownloadsV1 : AdminService<ApplyActivityDownloadsParametersV1, ApplyActivityDownloadsDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly DownloadStorage _downloads;
        private readonly TimeProvider _time;
        private readonly IDataProtector _protector;

        internal ApplyActivityDownloadsV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadStorage downloads, TimeProvider time, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
            _downloads = downloads;
            _time = time;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ApplyActivityDownloadsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ApplyActivityDownloadsParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var action = Data.DownloadBulkActionTypeId;
            foreach (var id in Data.OperationIds.Distinct())
            {
                if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadActionAsync(id, ct), CancellationToken) is not { } download)
                {
                    continue;
                }

                // Wait or go on, read a waiting one again, or take it out.
                switch (action)
                {
                    case DownloadBulkActionType.Pause or DownloadBulkActionType.Resume:
                        await DownloadActions.SetPausedAsync(Context, _sabnzbd, _qbittorrent, _protector, download, action == DownloadBulkActionType.Pause, CancellationToken);
                        break;
                    case DownloadBulkActionType.Retry:
                        await Context.ExecuteBatchAsync((ctx, ct) => ctx.RetryDownloadImportAsync(new ActivityOperationData(id), ct), CancellationToken);
                        break;
                    case DownloadBulkActionType.MoveToTop:
                        await DownloadActions.MoveAsync(Context, _sabnzbd, _qbittorrent, _protector, download, null, true, CancellationToken);
                        break;
                    default:
                        await DownloadActions.RemoveAsync(Context, _sabnzbd, _qbittorrent, _downloads, _protector, _time.GetUtcNow().UtcDateTime, download,
                            action == DownloadBulkActionType.RemoveAndBlock, CancellationToken);
                        break;
                }
            }

            return new UpdateResult(true);
        }
    }

    // The chosen downloads take one priority, each where its client has priorities.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/downloads/priority")]
    public sealed class PrioritizeActivityDownloadsV1 : AdminService<ApplyActivityDownloadsParametersV1, PrioritizeActivityDownloadsDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly IDataProtector _protector;

        internal PrioritizeActivityDownloadsV1(SabnzbdClient sabnzbd, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ApplyActivityDownloadsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ApplyActivityDownloadsParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            foreach (var id in Data.OperationIds.Distinct())
            {
                if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadActionAsync(id, ct), CancellationToken) is { } download)
                {
                    await DownloadActions.PrioritizeAsync(Context, _sabnzbd, _protector, download, Data.DownloadPriorityTypeId, CancellationToken);
                }
            }

            return new UpdateResult(true);
        }
    }

    // A download dragged to another place of its client's queue.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/move")]
    public sealed class MoveActivityDownloadV1 : AdminService<ActivityOperationParametersV1, MoveActivityDownloadDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly IDataProtector _protector;

        internal MoveActivityDownloadV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, IDataProtectionProvider protection)
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
            if (!await DownloadActions.MoveAsync(Context, _sabnzbd, _qbittorrent, _protector, download, Data.BeforeOperationId, false, CancellationToken))
            {
                throw new DataValidationException(nameof(MoveActivityDownloadDataV1.BeforeOperationId), "invalid_transition");
            }

            return new UpdateResult(true);
        }
    }

    // Finished downloads leave the history: the chosen ones, or every completed one of a client (of all with none named).
    [ServiceEndpoint("POST", "/api/v1/admin/activity/history/delete")]
    public sealed class DeleteActivityHistoryV1 : AdminService<ApplyActivityDownloadsParametersV1, DeleteActivityHistoryDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ApplyActivityDownloadsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ApplyActivityDownloadsParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var deleted = await Context.ExecuteBatchAsync((ctx, ct) => ctx.DeleteFinishedDownloadsAsync(new DownloadHistoryDeletionData(Data.OperationIds), ct), CancellationToken);
            return new UpdateResult(deleted > 0);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/history/clear")]
    public sealed class ClearActivityHistoryV1 : AdminService<DownloadClientParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DownloadClientParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadClientParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var deleted = await Context.ExecuteBatchAsync((ctx, ct) => ctx.DeleteFinishedDownloadsAsync(
                new DownloadHistoryDeletionData(null, Parameters.DownloadClientKindId, Parameters.AcquisitionConnectionId), ct), CancellationToken);
            return new UpdateResult(deleted > 0);
        }
    }

    // A client's speed limit: SABnzbd's and qBittorrent's own, or the workspace's for Jularr's Usenet and torrent downloaders.
    [ServiceEndpoint("GET", "/api/v1/admin/activity/clients/speed-limit")]
    public sealed class ReadDownloadSpeedLimitV1 : AdminService<DownloadClientParametersV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly IDataProtector _protector;

        internal ReadDownloadSpeedLimitV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DownloadSpeedLimitV1>()];
        protected override ModuleRequirement GetInstanceModules(DownloadClientParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadClientParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            switch (Parameters.DownloadClientKindId)
            {
                case DownloadClientKind.Usenet or DownloadClientKind.Torrent:
                    var settings = await Context.ReadDownloadStorageSettingsAsync(Parameters.DownloadClientKindId == DownloadClientKind.Usenet ? DownloadStorageArea.Usenet : DownloadStorageArea.Torrent,
                        CancellationToken);
                    return new DownloadSpeedLimitV1(settings.MaxBytesPerSecond);
                case DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent when Parameters.AcquisitionConnectionId is { } id:
                    var connection = DownloadSpeedLimits.Require(await Context.ReadAcquisitionConnectionAsync(id, CancellationToken), Parameters.DownloadClientKindId.Value);
                    try
                    {
                        return new DownloadSpeedLimitV1(Parameters.DownloadClientKindId == DownloadClientKind.Sabnzbd
                            ? await _sabnzbd.GetSpeedLimitAsync(DownloadActions.Connection(connection, _protector), CancellationToken)
                            : await _qbittorrent.GetDownloadLimitAsync(DownloadActions.TorrentConnection(connection, _protector), CancellationToken) is > 0 and var limit ? limit : null);
                    }
                    catch (Exception exception) when (DownloadActions.IsUnavailable(exception, CancellationToken))
                    {
                        throw new ServiceUnavailableException("acquisition_unavailable");
                    }
                default:
                    throw new DataValidationException(nameof(DownloadClientParametersV1.DownloadClientKindId), "invalid_transition");
            }
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/clients/speed-limit")]
    public sealed class SetDownloadSpeedLimitV1 : AdminService<DownloadClientParametersV1, SetDownloadSpeedLimitDataV1>
    {
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly DownloadSpeeds _speeds;
        private readonly IDataProtector _protector;

        internal SetDownloadSpeedLimitV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadSpeeds speeds, IDataProtectionProvider protection)
        {
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
            _speeds = speeds;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DownloadClientParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadClientParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            switch (Parameters.DownloadClientKindId)
            {
                case DownloadClientKind.Usenet or DownloadClientKind.Torrent:
                    var area = Parameters.DownloadClientKindId == DownloadClientKind.Usenet ? DownloadStorageArea.Usenet : DownloadStorageArea.Torrent;
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.SetDownloadSpeedLimitAsync(new DownloadSpeedLimitData(area, Data.MaxBytesPerSecond), ct);
                        return true;
                    }, CancellationToken);
                    break;
                case DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent when Parameters.AcquisitionConnectionId is { } id:
                    var connection = DownloadSpeedLimits.Require(await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(id, ct), CancellationToken),
                        Parameters.DownloadClientKindId.Value);
                    try
                    {
                        if (Parameters.DownloadClientKindId == DownloadClientKind.Sabnzbd) await _sabnzbd.SetSpeedLimitAsync(DownloadActions.Connection(connection, _protector), Data.MaxBytesPerSecond, CancellationToken);
                        else await _qbittorrent.SetDownloadLimitAsync(DownloadActions.TorrentConnection(connection, _protector), Data.MaxBytesPerSecond ?? 0, CancellationToken);
                    }
                    catch (Exception exception) when (DownloadActions.IsUnavailable(exception, CancellationToken))
                    {
                        throw new ServiceUnavailableException("acquisition_unavailable");
                    }

                    break;
                default:
                    throw new DataValidationException(nameof(DownloadClientParametersV1.DownloadClientKindId), "invalid_transition");
            }

            return new UpdateResult(true);
        }

        // Jularr's own downloaders hold back at once, not only from their next pass.
        protected override void OnCommitted()
        {
            if (Parameters.DownloadClientKindId == DownloadClientKind.Usenet) _speeds.Usenet.BytesPerSecond = Data.MaxBytesPerSecond ?? 0;
            if (Parameters.DownloadClientKindId == DownloadClientKind.Torrent) _speeds.Torrent.BytesPerSecond = Data.MaxBytesPerSecond ?? 0;
        }
    }

    internal static class DownloadSpeedLimits
    {
        // The SABnzbd or qBittorrent the parameters name; another connection is refused.
        public static AcquisitionConnectionData Require(AcquisitionConnectionData connection, DownloadClientKind kind) =>
            DownloadClientKinds.Of(connection.AcquisitionConnectionTypeId) == kind ? connection
                : throw new DataValidationException(nameof(DownloadClientParametersV1.AcquisitionConnectionId), "invalid_transition");
    }

    // A download added by hand for a wanted title: an NZB address for a Usenet client, a torrent address or a magnet for a torrent client. It goes the way of a grab, to the named client only.
    [ServiceEndpoint("POST", "/api/v1/admin/activity/downloads/add")]
    public sealed class AddActivityDownloadV1 : AdminService<DownloadClientParametersV1, AddActivityDownloadDataV1>
    {
        private readonly WantedAcquisition _acquisition;

        internal AddActivityDownloadV1(WantedAcquisition acquisition)
        {
            _acquisition = acquisition;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DownloadClientParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadClientParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // What the address is: a magnet, a .torrent or an NZB over HTTP(S).
            var address = Data.DownloadUrl.Trim();
            var magnet = address.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !magnet && uri.Scheme is not ("http" or "https"))
            {
                throw new DataValidationException(nameof(AddActivityDownloadDataV1.DownloadUrl), "download_address");
            }

            var torrent = magnet || uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
            var kind = Parameters.DownloadClientKindId;
            if (kind is { } named && !(torrent ? DownloadClientKinds.AddsTorrent(named) : DownloadClientKinds.AddsNzb(named)))
            {
                throw new DataValidationException(nameof(AddActivityDownloadDataV1.DownloadUrl), "download_address");
            }

            // The named client takes it, else the best client of its kind as for any grab.
            var (target, clients) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadWantedTargetAsync(Data.WantedItemId, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, ct)), CancellationToken);
            var chosen = clients.Where(client => kind is null || DownloadClientKinds.Of(client.AcquisitionConnectionTypeId) == kind
                && (Parameters.AcquisitionConnectionId is not { } id || client.Id == id)).ToArray();
            if (chosen.Length == 0)
            {
                throw new ServiceUnavailableException("acquisition_no_download_client");
            }

            var title = Title(uri, magnet) ?? target.CanonicalTitle;
            // The address names the release: the same one added twice is the same release.
            var identity = "manual:" + Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(address)))[..32].ToLowerInvariant();
            var offer = new ReleaseOffer(title, 0, "", 0, identity, uri, null, null, null, []) { Transport = torrent ? DownloadTransport.Torrent : DownloadTransport.Usenet };
            var quality = AcquisitionSelectionLogic.QualityKey(AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId), title, target.PlatformIgdbId);
            await _acquisition.GrabAsync(Context, target, offer, quality, chosen, CancellationToken, Auth.AccountId);
            return new UpdateResult(true);
        }

        // The release's name: a magnet's display name, else the file name of the address.
        private static string? Title(Uri uri, bool magnet)
        {
            if (magnet)
            {
                var name = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).FirstOrDefault(part => part.Length == 2 && part[0] == "dn");
                return name is null ? null : Uri.UnescapeDataString(name[1].Replace('+', ' '));
            }

            var file = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(file) ? null : file;
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
