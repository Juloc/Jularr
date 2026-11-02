using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Network;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Data.Torrent;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // One cycle over Jularr's own torrent downloads. With the module off it reads nothing more and says so, and the worker then waits for a grab.
    public sealed class DownloadTorrentsV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private readonly RunTorrentJobV1 _runner;
        private readonly DownloadSpeeds _speeds;

        internal DownloadTorrentsV1(RunTorrentJobV1 runner, DownloadSpeeds speeds)
        {
            _runner = runner;
            _speeds = speeds;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<TorrentCycleResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (modules, downloads, settings) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["Acquisition", "Torrent"]), ct),
                await ctx.ReadActiveDownloadsAsync(ct), await ctx.ReadDownloadStorageSettingsAsync(DownloadStorageArea.Torrent, ct)), CancellationToken);
            if (modules.Count(module => module.IsEnabled) != 2) return new TorrentCycleResultV1(false, false);
            _speeds.Torrent.BytesPerSecond = settings.MaxBytesPerSecond ?? 0;

            var jobs = new List<DownloadData>();
            foreach (var group in downloads.Where(download => download.ExternalId == TorrentJob.ExternalId).GroupBy(download => download.AcquisitionConnectionId))
            {
                var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(group.Key, ct), CancellationToken);
                if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent) jobs.AddRange(group);
            }

            var progressed = 0;
            await Parallel.ForEachAsync(jobs.Where(job => !job.IsPaused), new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallelJobs, CancellationToken = CancellationToken },
                async (download, ct) =>
                {
                    if ((await _runner.ExecuteAsync<UpdateResult>(new DownloadJobParametersV1(download.OperationId), default, ct)).Success) Interlocked.Exchange(ref progressed, 1);
                });
            return new TorrentCycleResultV1(progressed != 0, jobs.Count > 0);
        }
    }

    public sealed class RunTorrentJobV1 : SystemService<DownloadJobParametersV1, NoData>
    {
        private static readonly TimeSpan PassLength = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan WaitingRest = TimeSpan.FromMinutes(5);
        private const string WaitingName = "job.waiting";
        private readonly TorrentJob _jobs;
        private readonly DownloadImport _import;
        private readonly TimeProvider _time;
        private readonly DownloadStorage _downloads;
        private readonly ILogger<RunTorrentJobV1> _logger;

        internal RunTorrentJobV1(TorrentJob jobs, DownloadImport import, TimeProvider time, DownloadStorage downloads, ILogger<RunTorrentJobV1> logger)
        {
            _jobs = jobs;
            _import = import;
            _time = time;
            _downloads = downloads;
            _logger = logger;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DownloadJobParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Torrent");
        protected override ServicePermission GetPermission(DownloadJobParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var active = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadActiveDownloadsAsync(ct, Parameters.OperationId), CancellationToken);
            if (active.SingleOrDefault() is not { } download || download.ExternalId != TorrentJob.ExternalId || download.IsPaused) return new UpdateResult(false);
            var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
            if (connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Torrent) return new UpdateResult(false);
            DownloadStoragePaths paths;
            try
            {
                paths = await _downloads.OpenAsync(Context, DownloadStorageArea.Torrent, CancellationToken);
            }
            catch (ServiceUnavailableException exception)
            {
                await _import.AttentionAsync(Context, download.OperationId, exception.Code, null, CancellationToken);
                return new UpdateResult(false);
            }

            using var lease = paths.TryStartJob(download.OperationId);
            if (lease is null) return new UpdateResult(false);
            var staging = paths.Incomplete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId);
            var waiting = Path.Combine(staging, WaitingName);
            if (File.Exists(waiting) && _time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(waiting) < WaitingRest) return new UpdateResult(false);
            return new UpdateResult(await RunAsync(download, paths, staging));
        }

        private async Task<bool> RunAsync(DownloadData download, DownloadStoragePaths paths, string staging)
        {
            TorrentPassResult pass;
            try
            {
                pass = paths.IsComplete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId)
                    ? new TorrentPassResult(TorrentPassState.Completed, 100, 0, 0, 0, 0, false, outputPath: paths.Complete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId))
                    : await _jobs.RunAsync(Context, staging, _time.GetUtcNow() + PassLength, CancellationToken);
            }
            catch (TunnelUnavailableException)
            {
                // Fail-closed: without a verified tunnel the job sends nothing and rests; the client card names the tunnel's state.
                await HintAsync(download, "vpn_unavailable");
                await RestAsync(download, staging);
                return false;
            }
            catch (InvalidDataException exception)
            {
                _logger.LogError(exception, "Torrent job {OperationId} failed: the torrent data is invalid.", download.OperationId);
                await _import.FailAsync(Context, download.OperationId, DownloadLogic.FailedReasonKey, _time.GetUtcNow().UtcDateTime, CancellationToken);
                return false;
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Torrent job {OperationId} waits: its download storage is unavailable.", download.OperationId);
                await _import.AttentionAsync(Context, download.OperationId, "download_storage_unavailable", null, CancellationToken);
                return false;
            }

            switch (pass.State)
            {
                case TorrentPassState.Downloading:
                    var progress = new[]
                    {
                        new
                        {
                            download.OperationId, pass.ProgressPercent, pass.SizeBytes, pass.RemainingBytes, EtaSeconds = (int?)null, DownloadStateTypeId = (byte)DownloadStateType.Downloading
                        }
                    };
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.UpdateDownloadProgressAsync(new DownloadProgressData(JsonSerializer.Serialize(progress)), ct);
                        return true;
                    }, CancellationToken);
                    await HintAsync(download, pass.Progressed ? null : pass.ReasonKey);
                    if (!pass.Progressed && pass.ReasonKey is not null) await RestAsync(download, staging);
                    return pass.Progressed;
                case TorrentPassState.Failed:
                    _logger.LogError("Torrent job {OperationId} failed: {Reason}.", download.OperationId, pass.ReasonKey ?? "no reason given");
                    await _import.FailAsync(Context, download.OperationId, DownloadLogic.FailedReasonKey, _time.GetUtcNow().UtcDateTime, CancellationToken);
                    return false;
                default:
                    await HintAsync(download, null);
                    try
                    {
                        var extensions = LibraryFileTypes.GetImportExtensions(download.MediaTypeId, download.MediaSubtypeId);
                        var complete = await paths.CompleteAsync(download.OperationId, pass.OutputPath!, extensions, CancellationToken, download.MediaTypeId, download.MediaSubtypeId);
                        var state = new StorageScanState();
                        var files = StorageFileSystem.EnumerateFiles(complete, extensions, state, CancellationToken).ToArray();
                        await _import.PlaceAsync(Context, download, new LocalStorageFiles(complete), new ImportSourceRef(null, complete, ""), files, state.IsComplete, CancellationToken);
                    }
                    catch (ServiceUnavailableException exception)
                    {
                        await _import.AttentionAsync(Context, download.OperationId, exception.Code, null, CancellationToken);
                        return false;
                    }
                    catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                    {
                        await _import.AttentionAsync(Context, download.OperationId, "download_storage_unavailable", null, CancellationToken);
                        return false;
                    }

                    if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsDownloadImportedAsync(new DownloadOperationParameters(download.OperationId), ct), CancellationToken))
                    {
                        paths.RemoveCompleted(download.OperationId);
                        return true;
                    }

                    await RestAsync(download, staging);
                    return false;
            }
        }

        // Why a running job does not move; the next pass that moves data clears it.
        private async Task HintAsync(DownloadData download, string? hintKey) =>
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SetDownloadHintAsync(new DownloadHintData(download.OperationId, hintKey), ct);
                return true;
            }, CancellationToken);

        // A job without a tunnel or without peers, or an import that waits for a person, looks again after a rest.
        private async Task RestAsync(DownloadData download, string staging)
        {
            try
            {
                Directory.CreateDirectory(staging);
                await File.WriteAllTextAsync(Path.Combine(staging, WaitingName), "", CancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await _import.AttentionAsync(Context, download.OperationId, "download_storage_unavailable", null, CancellationToken);
            }
        }
    }
}
