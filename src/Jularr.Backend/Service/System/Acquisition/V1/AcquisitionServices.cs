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
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Sabnzbd;
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
                await ctx.ReadEnabledSearchConnectionsAsync(ct), await ctx.ReadDownloadClientsAsync(ct)),
                CancellationToken);
            if (indexers.Count == 0)
            {
                return new UpdateResult(due.Count == 0);
            }

            foreach (var item in due)
            {
                var target = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWantedTargetAsync(item.WantedItemId, ct), CancellationToken);
                if (target.Definition is null || AcquisitionSelectionLogic.GetKind(target.MediaTypeId) is null)
                {
                    await RecordAsync(target.WantedItemId, "profile_missing", now.AddDays(1));
                    continue;
                }

                // Series and Manga with volumes are searched by their missing units.
                if (target.MediaTypeId is MediaType.Series or MediaType.Manga)
                {
                    var units = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadUnitStatesAsync(target.WantedItemId, ct), CancellationToken);
                    if (target.MediaTypeId == MediaType.Series || units.Count > 0)
                    {
                        await SearchUnitsAsync(target, units, indexers, clients, now);
                        continue;
                    }
                }

                var result = await _acquisition.SearchAsync(target, indexers, SearchDepth.Normal, CancellationToken);
                if (result.Selection.Winner is not { } winner)
                {
                    // Indexer problems retry soon; empty results back off.
                    await RecordAsync(target.WantedItemId, result.OutcomeKey, result.IndexersUnavailable ? now.AddMinutes(30) : now + Backoff(target.SearchCount));
                    continue;
                }

                await RecordAsync(target.WantedItemId, result.OutcomeKey, now + Backoff(target.SearchCount));
                try
                {
                    await _acquisition.GrabAsync(Context, target, result.Offers[winner.Candidate.Id], winner.Score?.QualityKey, clients, CancellationToken);
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
            foreach (var parentNumber in units.Where(unit => unit.IsMissing).Select(unit => unit.ParentNumber).Distinct().Take(GroupsPerPass).ToArray())
            {
                for (var attempt = 0; attempt < GrabsPerGroup; attempt++)
                {
                    if (AcquisitionSelectionLogic.ReadNextUnitScope(units, parentNumber, attempt == 0) is not { } scope)
                    {
                        break;
                    }

                    var result = await _acquisition.SearchAsync(target, indexers, SearchDepth.Normal, CancellationToken, scope);
                    unavailable |= result.IndexersUnavailable;
                    if (result.Selection.Winner is not { } winner)
                    {
                        outcome ??= result.OutcomeKey;
                        break;
                    }

                    try
                    {
                        await _acquisition.GrabAsync(Context, target, result.Offers[winner.Candidate.Id], winner.Score?.QualityKey, clients, CancellationToken);
                    }
                    catch (ServiceUnavailableException exception)
                    {
                        await RecordAsync(target.WantedItemId, exception.Code, now.AddMinutes(30));
                        return;
                    }

                    grabbed++;
                    units = AcquisitionSelectionLogic.Claim(units, scope, target.MediaTypeId, winner.Candidate.Parsed);
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
        private readonly MangaMetadata _manga;

        internal RefreshWantedMetadataV1(WorkMetadata metadata, MangaMetadata manga)
        {
            _metadata = metadata;
            _manga = manga;
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
                        JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = wanted."WorkId" AND identity."Namespace" IN ('tmdb_tv', 'anilist')
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
                foreach (var identity in stale.Where(identity => identity.Namespace == MangaMetadata.Namespace))
                {
                    try
                    {
                        await _manga.ImportAsync(Context, identity.Value, CancellationToken);
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

                if (stale.Any(identity => identity.Namespace != MangaMetadata.Namespace))
                {
                    var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                    foreach (var identity in stale.Where(identity => identity.Namespace != MangaMetadata.Namespace))
                    {
                        await _metadata.ImportAsync(Context, connection, MediaType.Series, int.Parse(identity.Value, CultureInfo.InvariantCulture), CancellationToken);
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

    // Jularr's own Usenet downloader: one job per call for a bounded time; Success tells the worker that work remains.
    public sealed class DownloadUsenetV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private static readonly TimeSpan PassLength = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan InterruptedRetry = TimeSpan.FromMinutes(30);
        private readonly UsenetJob _jobs;
        private readonly DownloadImport _import;
        private readonly StorageOptions _options;
        private readonly TimeProvider _time;

        internal DownloadUsenetV1(UsenetJob jobs, DownloadImport import, IOptions<StorageOptions> options, TimeProvider time)
        {
            _jobs = jobs;
            _import = import;
            _options = options.Value;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The oldest job of a news server goes first.
            var downloads = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadActiveDownloadsAsync(ct), CancellationToken);
            foreach (var download in downloads.Where(download => download.ExternalId == UsenetJob.ExternalId).OrderBy(download => download.StartedAt))
            {
                var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
                if (connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Usenet)
                {
                    continue;
                }

                await RunAsync(download, connection);
                return new UpdateResult(true);
            }

            return new UpdateResult(false);
        }

        private async Task RunAsync(DownloadData download, AcquisitionConnectionData connection)
        {
            var staging = WantedAcquisition.Staging(_options, download.OperationId);
            UsenetPassResult pass;
            try
            {
                pass = await _jobs.RunAsync(connection, staging, _time.GetUtcNow() + PassLength, CancellationToken);
            }
            catch (UsenetException exception) when (exception.Failure == UsenetFailure.Unauthorized)
            {
                await _import.AttentionAsync(Context, download.OperationId, "acquisition_authentication_failed", null, CancellationToken);
                return;
            }
            catch (Exception exception) when (exception is UsenetException or IOException)
            {
                // A lost NZB or an unreachable server retries the release later.
                await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey, _time.GetUtcNow().UtcDateTime + InterruptedRetry, CancellationToken);
                return;
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
                    return;
                case UsenetPassState.Waiting:
                    await _import.AttentionAsync(Context, download.OperationId, pass.ReasonKey!, null, CancellationToken);
                    return;
                case UsenetPassState.Failed:
                    await _import.FailAsync(Context, download.OperationId, DownloadLogic.FailedReasonKey, _time.GetUtcNow().UtcDateTime, CancellationToken);
                    break;
                default:
                    var state = new StorageScanState();
                    var files = StorageFileSystem.EnumerateFiles(pass.OutputPath!, LibraryFileTypes.GetScanExtensions(download.MediaTypeId), state, CancellationToken).ToArray();
                    await _import.PlaceAsync(Context, download, pass.OutputPath!, files, state.IsComplete, CancellationToken);
                    break;
            }

            // Staging is kept only while the import waits for the Owner.
            if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsDownloadFinishedAsync(new DownloadOperationParameters(download.OperationId), ct), CancellationToken))
            {
                Directory.Delete(staging, recursive: true);
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
        private readonly IDataProtector _protector;
        private readonly StorageOptions _options;
        private readonly TimeProvider _time;
        private readonly DownloadImport _import;
        private readonly DirectSources _direct;

        internal MonitorDownloadsV1(SabnzbdClient sabnzbd, DirectSources direct, DownloadImport import, IDataProtectionProvider protection, IOptions<StorageOptions> options, TimeProvider time)
        {
            _direct = direct;
            _sabnzbd = sabnzbd;
            _import = import;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _options = options.Value;
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
                    foreach (var download in group.Take(DirectPerCycle))
                    {
                        await DownloadDirectAsync(download, connection);
                    }

                    continue;
                }

                // Jularr's own Usenet downloads run in DownloadUsenetV1.
                if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
                {
                    continue;
                }

                var client = new SabnzbdConnection(connection.BaseUrl, AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey));
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

            return new UpdateResult(true);
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
            if (storagePath is null || connection.RemotePath is null || connection.LocalPath is null
                || ImportPlacementLogic.MapRemotePath(storagePath, connection.RemotePath) is not { } relative)
            {
                await _import.AttentionAsync(Context, download.OperationId, "import_path_unmapped", null, CancellationToken);
                return;
            }

            // Read the job below the mapped folder without following links.
            IReadOnlyList<StorageFile> files;
            string source;
            var state = new StorageScanState();
            try
            {
                var mapped = StorageFileSystem.ResolveDirectory(connection.LocalPath, "");
                var full = Path.GetFullPath(Path.Combine(mapped, relative));
                if (File.Exists(full))
                {
                    source = StorageFileSystem.ResolveDirectory(mapped, Path.GetDirectoryName(relative) ?? "");
                    var file = new FileInfo(StorageFileSystem.ResolveFile(source, Path.GetFileName(full)));
                    files = [new StorageFile(file.Name, file.Length, file.LastWriteTimeUtc)];
                }
                else
                {
                    source = StorageFileSystem.ResolveDirectory(mapped, relative);
                    files = StorageFileSystem.EnumerateFiles(source, LibraryFileTypes.GetScanExtensions(download.MediaTypeId), state, CancellationToken).ToArray();
                }
            }
            catch (NasMountException)
            {
                await _import.AttentionAsync(Context, download.OperationId, "import_source_unavailable", null, CancellationToken);
                return;
            }

            await _import.PlaceAsync(Context, download, source, files, state.IsComplete, CancellationToken);
        }

        // Jularr downloads direct-source books itself into staging, then imports them like any other job.
        private async Task DownloadDirectAsync(DownloadData download, AcquisitionConnectionData connection)
        {
            var staging = Path.Combine(_options.DirectDownloadPath, download.OperationId.ToString(CultureInfo.InvariantCulture));
            var name = "book." + DirectSources.Format(download.ExternalId ?? "");
            var path = Path.Combine(staging, name);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(staging);
                try
                {
                    File.Delete(path + ".part");
                    await _direct.DownloadAsync(connection, download, path + ".part", CancellationToken);
                    File.Move(path + ".part", path);
                }
                catch (DirectSourceException exception) when (exception.ErrorKey is "source_gate" or DirectSources.ManualLinkKey)
                {
                    // A login or challenge waits for a person; nothing is bypassed.
                    await _import.AttentionAsync(Context, download.OperationId, exception.ErrorKey, exception.ManualUrl?.ToString(), CancellationToken);
                    return;
                }
                catch (Exception exception) when (exception is DirectSourceException or IOException)
                {
                    File.Delete(path + ".part");
                    await _import.FailAsync(Context, download.OperationId, DownloadLogic.InterruptedReasonKey, _time.GetUtcNow().UtcDateTime + InterruptedRetry, CancellationToken);
                    return;
                }
            }

            var file = new FileInfo(path);
            await _import.PlaceAsync(Context, download, staging, [new StorageFile(name, file.Length, file.LastWriteTimeUtc)], true, CancellationToken);

            // Staging is kept only while the import waits for the Owner.
            var finished = await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsDownloadFinishedAsync(new DownloadOperationParameters(download.OperationId), ct), CancellationToken);
            if (finished)
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }
}
