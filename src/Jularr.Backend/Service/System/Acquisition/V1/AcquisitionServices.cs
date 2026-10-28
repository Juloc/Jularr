using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Qbittorrent;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    public sealed class SearchDueWantedV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private const int BatchSize = 5;
        private const int GroupsPerPass = 3;
        private const int GrabsPerGroup = 5;
        private readonly WantedAcquisition _acquisition;
        private readonly TimeProvider _time;

        internal SearchDueWantedV1(WantedAcquisition acquisition, TimeProvider time)
        {
            _acquisition = acquisition;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var (due, indexers, clients) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await ctx.ReadDueWantedItemsAsync(new WantedDueData(now, BatchSize), ct),
                await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, ct)),
                CancellationToken);
            if (indexers.Count == 0)
            {
                return new UpdateResult(due.Count == 0);
            }

            foreach (var item in due)
            {
                // Indexers are read per search, so today's counts and backoffs stay current through the cycle.
                (var target, indexers) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    (await ctx.ReadWantedTargetAsync(item.WantedItemId, ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, ct)), CancellationToken);
                if (target.Definition is null)
                {
                    await RecordAsync(target.WantedItemId, "profile_missing", now.AddDays(1));
                    continue;
                }

                // Series and Manga with volumes are searched by their missing units.
                if (target.MediaTypeId == MediaType.Series || target.MediaSubtypeId == MediaSubtype.Manga)
                {
                    var units = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadUnitStatesAsync(target.WantedItemId, ct), CancellationToken);
                    if (target.MediaTypeId == MediaType.Series || units.Count > 0)
                    {
                        await SearchUnitsAsync(target, units, indexers, clients, now);
                        continue;
                    }
                }

                var result = await _acquisition.SearchAsync(Context, target, indexers, SearchDepth.Normal, automatic: true, CancellationToken);
                if (result.Selection.Winner is not { } winner)
                {
                    // Indexer problems retry soon; empty results back off, an upgrade is looked for at its own interval.
                    await RecordAsync(target.WantedItemId, result.OutcomeKey, result.IndexersUnavailable ? now.AddMinutes(30)
                        : target.HasMedia ? now + UpgradePolicy.SearchInterval : now + Backoff(target.SearchCount));
                    continue;
                }

                await RecordAsync(target.WantedItemId, result.OutcomeKey, now + Backoff(target.SearchCount));
                try
                {
                    await _acquisition.GrabAsync(Context, target, result.Offers[winner.Candidate.Id], winner.Score?.QualityKey, clients, CancellationToken, automatic: true);
                }
                catch (ServiceUnavailableException exception)
                {
                    await RecordAsync(target.WantedItemId, exception.Code, now.AddMinutes(30));
                }
            }

            return new UpdateResult(true);
        }

        private async Task SearchUnitsAsync(WantedTargetData target, IReadOnlyList<UnitStateData> units, IReadOnlyList<AcquisitionConnectionData> indexers,
            IReadOnlyList<AcquisitionConnectionData> clients, DateTime now)
        {
            var grabbed = 0;
            var unavailable = false;
            string? outcome = null;
            foreach (var parentNumber in units.Where(unit => unit.NeedsSearch).Select(unit => unit.ParentNumber).Distinct().Take(GroupsPerPass).ToArray())
            {
                for (var attempt = 0; attempt < GrabsPerGroup; attempt++)
                {
                    if (AcquisitionSelectionLogic.ReadNextUnitScope(units, parentNumber, attempt == 0) is not { } scope)
                    {
                        break;
                    }

                    indexers = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, ct), CancellationToken);
                    var result = await _acquisition.SearchAsync(Context, target, indexers, SearchDepth.Normal, automatic: true, CancellationToken, scope);
                    unavailable |= result.IndexersUnavailable;
                    if (result.Selection.Winner is not { } winner)
                    {
                        outcome ??= result.OutcomeKey;
                        break;
                    }

                    try
                    {
                        await _acquisition.GrabAsync(Context, target, result.Offers[winner.Candidate.Id], winner.Score?.QualityKey, clients, CancellationToken, automatic: true);
                    }
                    catch (ServiceUnavailableException exception)
                    {
                        await RecordAsync(target.WantedItemId, exception.Code, now.AddMinutes(30));
                        return;
                    }

                    grabbed++;
                    units = AcquisitionSelectionLogic.Claim(units, scope, AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId), winner.Candidate.Parsed);
                }
            }

            // Grabs continue soon; empty results back off.
            var nextSearch = grabbed > 0 ? now.AddMinutes(15) : unavailable ? now.AddMinutes(30) : now + Backoff(target.SearchCount);
            await RecordAsync(target.WantedItemId, grabbed > 0 ? "grabbed" : outcome ?? "no_candidates", nextSearch);
        }

        private Task RecordAsync(long wantedItemId, string outcomeKey, DateTime nextSearchAt) =>
            Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RecordWantedSearchAsync(new WantedSearchOutcomeData(wantedItemId, outcomeKey, nextSearchAt), ct);
                return true;
            }, CancellationToken);

        private static TimeSpan Backoff(int searchCount) => searchCount switch
        {
            0 => TimeSpan.FromHours(6),
            1 => TimeSpan.FromHours(12),
            _ => TimeSpan.FromHours(24)
        };
    }

    public sealed class RefreshWantedMetadataV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;
        private readonly AniListMetadata _aniList;

        internal RefreshWantedMetadataV1(WorkMetadata metadata, AniListMetadata aniList)
        {
            _metadata = metadata;
            _aniList = aniList;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Wanted series and Manga learn about new episodes and volumes once a day.
            var stale = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        identity."Namespace",
                        identity."Value",
                        identity."WorkId"
                    FROM
                        "WantedItems" AS wanted
                        JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                        JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = work."Id"
                            AND (identity."Namespace" = 'tmdb_tv' OR identity."Namespace" = 'anilist' AND work."MediaTypeId" = 3 AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WorkExternalIdentities" AS book
                                WHERE
                                    book."WorkId" = work."Id"
                                    AND book."Namespace" = 'openlibrary'
                            ))
                    WHERE
                        NOT wanted."IsPaused"
                        AND identity."UpdatedAt" < now() - interval '1 day'
                    ORDER BY
                        identity."UpdatedAt"
                    LIMIT 5;
                    """;
                return ctx.ReadSql.ReadListAsync<WorkIdentityData>(sql, ct);
            }, CancellationToken);
            if (stale.Count == 0)
            {
                return new UpdateResult(true);
            }

            // Without a reachable provider the Works keep their known units.
            try
            {
                foreach (var identity in stale.Where(identity => identity.Namespace == AniListMetadata.Namespace))
                {
                    try
                    {
                        await _aniList.ImportAsync(Context, identity.Value, CancellationToken);
                    }
                    catch (KeyNotFoundException)
                    {
                        await Context.ExecuteBatchAsync(async (ctx, ct) =>
                        {
                            await ctx.TouchWorkIdentityAsync(identity, ct);
                            return true;
                        }, CancellationToken);
                    }
                }

                if (stale.Any(identity => identity.Namespace != AniListMetadata.Namespace))
                {
                    var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                    foreach (var identity in stale.Where(identity => identity.Namespace != AniListMetadata.Namespace))
                    {
                        await _metadata.ImportAsync(Context, connection, MediaType.Series, null, int.Parse(identity.Value, CultureInfo.InvariantCulture), CancellationToken);
                    }
                }
            }
            catch (ServiceUnavailableException)
            {
                return new UpdateResult(false);
            }

            return new UpdateResult(true);
        }
    }

    // Parallel jobs use independent service invocations and SQL contexts.
    public sealed class DownloadUsenetV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private static readonly TimeSpan ReservationGrace = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan InterruptedRetry = TimeSpan.FromMinutes(30);
        private readonly RunUsenetJobV1 _runner;
        private readonly DownloadImport _import;
        private readonly TimeProvider _time;

        internal DownloadUsenetV1(RunUsenetJobV1 runner, DownloadImport import, TimeProvider time)
        {
            _runner = runner;
            _import = import;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (downloads, settings) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadActiveDownloadsAsync(ct), await ctx.ReadDownloadStorageSettingsAsync(DownloadStorageArea.Usenet, ct)), CancellationToken);
            var jobs = new List<DownloadData>();
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var group in downloads.GroupBy(download => download.AcquisitionConnectionId))
            {
                var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(group.Key, ct), CancellationToken);
                if (connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Usenet) continue;
                foreach (var stuck in group.Where(download => download.ExternalId is null && now - download.StartedAt > ReservationGrace))
                {
                    await _import.FailAsync(Context, stuck.OperationId, DownloadLogic.InterruptedReasonKey, now + InterruptedRetry, CancellationToken);
                }

                jobs.AddRange(group.Where(download => download.ExternalId == UsenetJob.ExternalId && !download.IsPaused));
            }

            var progressed = 0;
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallelJobs, CancellationToken = CancellationToken }, async (download, ct) =>
            {
                if ((await _runner.ExecuteAsync<UpdateResult>(new DownloadJobParametersV1(download.OperationId), default, ct)).Success) Interlocked.Exchange(ref progressed, 1);
            });
            return new UpdateResult(progressed != 0);
        }
    }

    public sealed class RunUsenetJobV1 : SystemService<DownloadJobParametersV1, NoData>
    {
        private static readonly TimeSpan PassLength = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan InterruptedRetry = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan WaitingRest = TimeSpan.FromMinutes(15);
        private const string WaitingName = "job.waiting";
        private readonly UsenetJob _jobs;
        private readonly DownloadImport _import;
        private readonly TimeProvider _time;
        private readonly DownloadStorage _downloads;

        internal RunUsenetJobV1(UsenetJob jobs, DownloadImport import, TimeProvider time, DownloadStorage downloads)
        {
            _downloads = downloads;
            _jobs = jobs;
            _import = import;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DownloadJobParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadJobParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var active = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadActiveDownloadsAsync(ct, Parameters.OperationId), CancellationToken);
            if (active.SingleOrDefault() is not { } download || download.ExternalId != UsenetJob.ExternalId || download.IsPaused) return new UpdateResult(false);
            var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
            if (connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Usenet) return new UpdateResult(false);
            DownloadStoragePaths paths;
            try
            {
                paths = await _downloads.OpenAsync(Context, DownloadStorageArea.Usenet, CancellationToken);
            }
            catch (ServiceUnavailableException exception)
            {
                await _import.AttentionAsync(Context, download.OperationId, exception.Code, null, CancellationToken);
                return new UpdateResult(false);
            }

            using var lease = paths.TryStartJob(download.OperationId);
            if (lease is null) return new UpdateResult(false);
            var waiting = Path.Combine(paths.Incomplete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId), WaitingName);
            if (File.Exists(waiting) && _time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(waiting) < WaitingRest) return new UpdateResult(false);
            return new UpdateResult(await RunAsync(download, connection, paths));
        }

        private async Task<bool> RunAsync(DownloadData download, AcquisitionConnectionData connection, DownloadStoragePaths paths)
        {
            var staging = paths.Incomplete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId);
            UsenetPassResult pass;
            try
            {
                pass = paths.IsComplete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId)
                    ? new UsenetPassResult(UsenetPassState.Completed, 100, 0, 0, outputPath: paths.Complete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId))
                    : await _jobs.RunAsync(connection, staging, _time.GetUtcNow() + PassLength, CancellationToken, paths, download.OperationId);
            }
            catch (UsenetException exception) when (exception.Failure == UsenetFailure.Unauthorized)
            {
                await WaitAsync(download, staging, "acquisition_authentication_failed");
                return false;
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
            catch (UsenetException)
            {
                // A lost NZB or an unreachable server retries the release later.
                await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey, _time.GetUtcNow().UtcDateTime + InterruptedRetry, CancellationToken);
                return false;
            }

            switch (pass.State)
            {
                case UsenetPassState.Downloading:
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
                    return pass.Progressed;
                case UsenetPassState.Waiting:
                    await WaitAsync(download, staging, pass.ReasonKey!);
                    return false;
                case UsenetPassState.Failed:
                    await _import.FailAsync(Context, download.OperationId, DownloadLogic.FailedReasonKey, _time.GetUtcNow().UtcDateTime, CancellationToken);
                    break;
                default:
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
                        await WaitAsync(download, staging, exception.Code);
                        return false;
                    }
                    catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                    {
                        await WaitAsync(download, staging, "download_storage_unavailable");
                        return false;
                    }
                    break;
            }

            // Staging is kept only while the import waits for the Owner; that wait rests like any other.
            if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsDownloadImportedAsync(new DownloadOperationParameters(download.OperationId), ct), CancellationToken))
            {
                paths.RemoveCompleted(download.OperationId);
                return true;
            }

            await MarkWaitingAsync(download, staging);
            return false;
        }

        private async Task WaitAsync(DownloadData download, string staging, string reasonKey)
        {
            await _import.AttentionAsync(Context, download.OperationId, reasonKey, null, CancellationToken);
            await MarkWaitingAsync(download, staging);
        }

        private async Task MarkWaitingAsync(DownloadData download, string staging)
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(staging, WaitingName), "", CancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await _import.AttentionAsync(Context, download.OperationId, "download_storage_unavailable", null, CancellationToken);
            }
        }
    }

    public sealed class MonitorDownloadsV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ReservationGrace = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan InterruptedRetry = TimeSpan.FromMinutes(30);
        private const int DirectPerCycle = 5;
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly IDataProtector _protector;
        private readonly TimeProvider _time;
        private readonly DownloadImport _import;
        private readonly StorageAccess _storage;
        private readonly RunDirectDownloadJobV1 _runner;

        internal MonitorDownloadsV1(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadImport import, IDataProtectionProvider protection, TimeProvider time, StorageAccess storage, RunDirectDownloadJobV1 runner)
        {
            _runner = runner;
            _storage = storage;
            _sabnzbd = sabnzbd;
            _qbittorrent = qbittorrent;
            _import = import;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var downloads = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadActiveDownloadsAsync(ct), CancellationToken);
            foreach (var group in downloads.GroupBy(download => download.AcquisitionConnectionId))
            {
                // An unreachable client keeps its downloads running.
                var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(group.Key, ct), CancellationToken);
                if (DirectSources.IsDirect(connection.AcquisitionConnectionTypeId))
                {
                    var settings = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadStorageSettingsAsync(DownloadStorageArea.Direct, ct), CancellationToken);
                    await Parallel.ForEachAsync(group.Take(DirectPerCycle),
                        new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallelJobs, CancellationToken = CancellationToken },
                        async (download, ct) => await _runner.ExecuteAsync<UpdateResult>(new DownloadJobParametersV1(download.OperationId), default, ct));

                    continue;
                }

                // Jularr's own Usenet downloads run in DownloadUsenetV1.
                if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
                {
                    continue;
                }

                // A torrent client reports the state of its torrents; the finished file is read from its folder.
                if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent)
                {
                    var torrentClient = new QbittorrentConnection(connection.HttpAddress, connection.Username,
                        AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedPassword));
                    QbittorrentSnapshot snapshot;
                    try
                    {
                        var hashes = group.Select(download => download.ExternalId).OfType<string>().ToArray();
                        snapshot = await _qbittorrent.GetSnapshotAsync(torrentClient, hashes, CancellationToken);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or QbittorrentException or JsonException
                        || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    var torrents = snapshot.Torrents.ToDictionary(torrent => torrent.Hash, StringComparer.Ordinal);
                    var torrentProgress = group.Where(download => download.ExternalId is not null && torrents.ContainsKey(download.ExternalId!))
                        .Select(download =>
                        {
                            var torrent = torrents[download.ExternalId!];
                            return new
                            {
                                download.OperationId,
                                ProgressPercent = (int?)Math.Clamp((int)Math.Round(torrent.Progress * 100), 0, 99),
                                SizeBytes = (long?)torrent.SizeBytes,
                                RemainingBytes = (long?)Math.Max(0, torrent.SizeBytes - torrent.DownloadedBytes),
                                EtaSeconds = torrent.DownloadSpeed > 0
                                    ? (int?)Math.Min(int.MaxValue, Math.Ceiling((double)Math.Max(0, torrent.SizeBytes - torrent.DownloadedBytes) / torrent.DownloadSpeed))
                                    : null,
                                DownloadStateTypeId = (byte)DownloadLogic.ReadDownloadState(torrent.IsPaused ? "Paused" : torrent.IsFailed ? "Failed" : "Downloading")
                            };
                        })
                        .ToArray();
                    if (torrentProgress.Length > 0)
                    {
                        await Context.ExecuteBatchAsync(async (ctx, ct) =>
                        {
                            await ctx.UpdateDownloadProgressAsync(new DownloadProgressData(JsonSerializer.Serialize(torrentProgress)), ct);
                            return true;
                        }, CancellationToken);
                    }

                    foreach (var download in group)
                    {
                        if (download.ExternalId is not { } hash || !torrents.TryGetValue(hash, out var torrent))
                        {
                            if (_time.GetUtcNow().UtcDateTime - download.StartedAt > LostAfter)
                                await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey,
                                    _time.GetUtcNow().UtcDateTime + InterruptedRetry, CancellationToken);
                            continue;
                        }

                        if (torrent.IsFailed)
                        {
                            await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey,
                                _time.GetUtcNow().UtcDateTime + InterruptedRetry, CancellationToken);
                        }
                        else if (torrent.IsCompleted)
                        {
                            // Import only the exact hash registered by the grab, never an adjacent torrent.
                            await ImportAsync(download, connection, torrent.ContentPath);
                        }
                    }

                    continue;
                }
                var client = new SabnzbdConnection(connection.HttpAddress, AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey));
                SabnzbdQueueSnapshot queue;
                SabnzbdHistorySnapshot history;
                try
                {
                    var ids = group.Select(download => download.ExternalId).OfType<string>().ToArray();
                    queue = await _sabnzbd.GetQueueAsync(client, ids, CancellationToken);
                    history = await _sabnzbd.GetHistoryAsync(client, ids, CancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or SabnzbdException or JsonException
                    || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                // Queue progress is written in one batch.
                var jobs = queue.Jobs.GroupBy(job => job.NzoId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                var progress = group.Where(download => download.ExternalId is not null && jobs.ContainsKey(download.ExternalId))
                    .Select(download =>
                    {
                        var job = jobs[download.ExternalId!];
                        return new
                        {
                            download.OperationId,
                            ProgressPercent = job.Percentage is { } value ? (int?)Math.Clamp((int)Math.Round(value), 0, 99) : null,
                            job.SizeBytes,
                            job.RemainingBytes,
                            EtaSeconds = job.TimeLeftSeconds,
                            DownloadStateTypeId = (byte)DownloadLogic.ReadDownloadState(job.Status)
                        };
                    })
                    .ToArray();
                if (progress.Length > 0)
                {
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.UpdateDownloadProgressAsync(new DownloadProgressData(JsonSerializer.Serialize(progress)), ct);
                        return true;
                    }, CancellationToken);
                }

                foreach (var download in group.Where(download => download.ExternalId is null || !jobs.ContainsKey(download.ExternalId)))
                {
                    if (download.ExternalId is null)
                    {
                        await RecoverAsync(download, client);
                    }
                    else
                    {
                        await TrackAsync(download, connection, history);
                    }
                }
            }

            // Whether downloads are still running tells the worker how soon to look again.
            var cleanups = await _import.ProcessCleanupAsync(Context, CancellationToken);
            return new UpdateResult(downloads.Count > 0 || cleanups > 0);
        }

        private async Task RecoverAsync(DownloadData download, SabnzbdConnection client)
        {
            // A grab interrupted between client and database is adopted or retried.
            var age = _time.GetUtcNow().UtcDateTime - download.StartedAt;
            if (age < ReservationGrace)
            {
                return;
            }

            string? nzoId;
            try
            {
                nzoId = await _sabnzbd.FindJobAsync(client, download.Title, CancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or SabnzbdException or JsonException
                || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (nzoId is not null)
            {
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.StartDownloadAsync(new DownloadStartData(download.OperationId, nzoId), ct);
                    return true;
                }, CancellationToken);
            }
            else if (age > LostAfter)
            {
                await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey, _time.GetUtcNow().UtcDateTime + InterruptedRetry, CancellationToken);
            }
        }

        private async Task TrackAsync(DownloadData download, AcquisitionConnectionData connection, SabnzbdHistorySnapshot history)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var finished = history.Jobs.FirstOrDefault(job => job.NzoId == download.ExternalId);
            if (finished is null)
            {
                // A job the client no longer knows is retried later.
                if (now - download.StartedAt > LostAfter)
                {
                    await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey, now + InterruptedRetry, CancellationToken);
                }

                return;
            }

            if (finished.IsFailed)
            {
                var releaseFault = SabnzbdFailureKinds.IsReleaseFault(finished.FailureKind);
                await _import.FailAsync(Context, download.OperationId, releaseFault ? DownloadLogic.FailedReasonKey : DownloadLogic.InterruptedReasonKey, releaseFault ? now : now + InterruptedRetry, CancellationToken);
                return;
            }

            // Post-processing continues until the client reports completion.
            if (finished.IsCompleted)
            {
                await ImportAsync(download, connection, finished.StoragePath);
                return;
            }

            var state = new[] { new { download.OperationId, ProgressPercent = (int?)99, SizeBytes = (long?)null, RemainingBytes = (long?)0, EtaSeconds = (int?)null, DownloadStateTypeId = (byte)DownloadStateType.PostProcessing } };
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.UpdateDownloadProgressAsync(new DownloadProgressData(JsonSerializer.Serialize(state)), ct);
                return true;
            }, CancellationToken);
        }

        private async Task ImportAsync(DownloadData download, AcquisitionConnectionData connection, string? storagePath)
        {
            // Only mapped client folders are read; otherwise the download waits for the Owner.
            if (storagePath is null || connection.RemotePath is null || connection.LocalPath is null && connection.DownloadStorageMountId is null
                || ImportPlacementLogic.MapRemotePath(storagePath, connection.RemotePath) is not { } relative)
            {
                await _import.AttentionAsync(Context, download.OperationId, "import_path_unmapped", null, CancellationToken);
                return;
            }

            // Read the job below the mapped folder without following links.
            IReadOnlyList<StorageFile> files;
            IReadOnlyList<StorageFile>? allSourceFiles = null;
            StorageFiles source;
            string sourceDirectory;
            long? sourceMountId = null;
            string? sourceRoot = null;
            var state = new StorageScanState();
            try
            {
                var prefix = "";
                if (connection.DownloadStorageMountId is { } storageMountId)
                {
                    var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(storageMountId, ct), CancellationToken);
                    source = _storage.Open(mount);
                    sourceMountId = storageMountId;
                    prefix = connection.LocalPath ?? "";
                }
                else
                {
                    sourceRoot = StorageFileSystem.ResolveDirectory(connection.LocalPath!, "");
                    source = new LocalStorageFiles(sourceRoot);
                }

                var path = StoragePath.Combine(prefix, relative);
                var entry = await source.GetAsync(path, CancellationToken) ?? throw new NasMountException("storage_unavailable");
                if (!entry.IsDirectory)
                {
                    sourceDirectory = StoragePath.Parent(path);
                    files = [new StorageFile(entry.Name, entry.Length, entry.ModifiedAt)];
                }
                else
                {
                    sourceDirectory = path;
                    var found = new List<StorageFile>();
                    await foreach (var file in source.EnumerateFilesAsync(path, null, state, CancellationToken))
                    {
                        found.Add(file);
                    }

                    allSourceFiles = found;
                    var extensions = LibraryFileTypes.GetImportExtensions(download.MediaTypeId, download.MediaSubtypeId);
                    files = extensions is null ? found : found.Where(file => extensions.Contains(Path.GetExtension(file.RelativePath).ToLowerInvariant())).ToArray();
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                await _import.AttentionAsync(Context, download.OperationId, "import_source_unavailable", null, CancellationToken);
                return;
            }

            await _import.PlaceAsync(Context, download, source, new ImportSourceRef(sourceMountId, sourceRoot, sourceDirectory), files, state.IsComplete, CancellationToken, allSourceFiles);
        }


    }

    public sealed class RunDirectDownloadJobV1 : SystemService<DownloadJobParametersV1, NoData>
    {
        private static readonly TimeSpan InterruptedRetry = TimeSpan.FromMinutes(30);
        private readonly DirectSources _direct;
        private readonly DownloadImport _import;
        private readonly DownloadStorage _downloads;
        private readonly TimeProvider _time;

        internal RunDirectDownloadJobV1(DirectSources direct, DownloadImport import, DownloadStorage downloads, TimeProvider time)
        {
            _direct = direct;
            _import = import;
            _downloads = downloads;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DownloadJobParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadJobParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var active = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadActiveDownloadsAsync(ct, Parameters.OperationId), CancellationToken);
            if (active.SingleOrDefault() is not { } download || download.IsPaused) return new UpdateResult(false);
            var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
            if (!DirectSources.IsDirect(connection.AcquisitionConnectionTypeId)) return new UpdateResult(false);
            DownloadStoragePaths paths;
            try
            {
                paths = await _downloads.OpenAsync(Context, DownloadStorageArea.Direct, CancellationToken);
            }
            catch (ServiceUnavailableException exception)
            {
                await _import.AttentionAsync(Context, download.OperationId, exception.Code, null, CancellationToken);
                return new UpdateResult(false);
            }

            using var lease = paths.TryStartJob(download.OperationId);
            if (lease is null) return new UpdateResult(false);
            await DownloadDirectAsync(download, connection, paths);
            return new UpdateResult(true);
        }

        // Jularr downloads direct-source books itself into staging, then imports them like any other job.
        private async Task DownloadDirectAsync(DownloadData download, AcquisitionConnectionData connection, DownloadStoragePaths paths)
        {
            var staging = paths.Incomplete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId);
            var name = "book." + DirectSources.Format(download.ExternalId ?? "");
            var path = Path.Combine(staging, name);
            if (!paths.IsComplete(download.OperationId, download.MediaTypeId, download.MediaSubtypeId) && !File.Exists(path))
            {
                try
                {
                    await paths.PrepareIncompleteAsync(download.OperationId, CancellationToken, download.MediaTypeId, download.MediaSubtypeId);
                    File.Delete(path + ".part");
                    if (await _direct.DownloadAsync(connection, download, path + ".part", paths, CancellationToken) is { } pass)
                    {
                        // A pass kept its part; the next job continues where it ended.
                        var progress = new[]
                        {
                            new
                            {
                                download.OperationId, ProgressPercent = pass.Total > 0 ? (short?)Math.Min(99, pass.Done * 100 / pass.Total) : null, SizeBytes = (long?)null,
                                RemainingBytes = (long?)null, EtaSeconds = (int?)null, DownloadStateTypeId = (byte)DownloadStateType.Downloading
                            }
                        };
                        await Context.ExecuteBatchAsync(async (ctx, ct) =>
                        {
                            await ctx.UpdateDownloadProgressAsync(new DownloadProgressData(JsonSerializer.Serialize(progress)), ct);
                            return true;
                        }, CancellationToken);
                        return;
                    }

                    // A file of an earlier attempt is replaced; it never stays beside the new one.
                    File.Delete(path);
                    File.Move(path + ".part", path);
                }
                catch (DirectSourceWaitException wait)
                {
                    // The source's own countdown runs between jobs, so waiting never holds a download slot.
                    var eta = Math.Max(1, (int)Math.Ceiling(wait.Wait.TotalSeconds));
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.SetDownloadWaitAsync(new DownloadWaitData(download.OperationId, _time.GetUtcNow().UtcDateTime + wait.Wait, eta), ct);
                        return true;
                    }, CancellationToken);
                    return;
                }
                catch (DirectSourceException exception) when (exception.ErrorKey is "source_gate" or DirectSources.ManualLinkKey)
                {
                    // A login or challenge waits for a person; nothing is bypassed.
                    await _import.AttentionAsync(Context, download.OperationId, exception.ErrorKey, exception.ManualUrl?.ToString(), CancellationToken);
                    return;
                }
                catch (ServiceUnavailableException exception)
                {
                    await _import.AttentionAsync(Context, download.OperationId, exception.Code, null, CancellationToken);
                    return;
                }
                catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
                {
                    await _import.AttentionAsync(Context, download.OperationId, "download_storage_unavailable", null, CancellationToken);
                    return;
                }
                catch (DirectSourceException)
                {
                    File.Delete(path + ".part");
                    // What the failed attempt left behind goes; the next one starts clean.
                    paths.RemoveCompleted(download.OperationId);
                    await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey, _time.GetUtcNow().UtcDateTime + InterruptedRetry, CancellationToken);
                    return;
                }
            }

            try
            {
                var complete = await paths.CompleteAsync(download.OperationId, staging, LibraryFileTypes.GetImportExtensions(download.MediaTypeId, download.MediaSubtypeId), CancellationToken, download.MediaTypeId, download.MediaSubtypeId);
                var file = new FileInfo(Path.Combine(complete, name));
                await _import.PlaceAsync(Context, download, new LocalStorageFiles(complete), new ImportSourceRef(null, complete, ""), [new StorageFile(name, file.Length, file.LastWriteTimeUtc)], true, CancellationToken);
            }
            catch (ServiceUnavailableException exception)
            {
                await _import.AttentionAsync(Context, download.OperationId, exception.Code, null, CancellationToken);
                return;
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                await _import.AttentionAsync(Context, download.OperationId, "download_storage_unavailable", null, CancellationToken);
                return;
            }

            // Preserve files awaiting review; terminal success and failure no longer need staging.
            var terminal = await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsDownloadTerminalAsync(new DownloadOperationParameters(download.OperationId), ct), CancellationToken);
            if (terminal)
            {
                paths.RemoveCompleted(download.OperationId);
            }
        }
    }
}
