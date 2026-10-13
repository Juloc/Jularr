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
using Jularr.Backend.Service.Admin.Media;
using Jularr.Backend.Service.Admin.Provider;
using Jularr.Backend.Service.Admin.Wanted;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Gutenberg;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Gutenberg;
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
        private const int SeasonsPerPass = 3;
        private const int GrabsPerSeason = 5;
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
                await ctx.ReadEnabledSearchConnectionsAsync(ct), await ctx.ReadEnabledAcquisitionConnectionsAsync(AcquisitionConnectionType.Sabnzbd, ct)),
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

                if (target.MediaTypeId == MediaType.Series)
                {
                    await SearchSeriesAsync(target, indexers, clients, now);
                    continue;
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

        private async Task SearchSeriesAsync(WantedTargetData target, IReadOnlyList<AcquisitionConnectionData> indexers, IReadOnlyList<AcquisitionConnectionData> clients, DateTime now)
        {
            var episodes = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadEpisodeStatesAsync(target.WantedItemId, ct), CancellationToken);
            var grabbed = 0;
            var unavailable = false;
            string? outcome = null;
            foreach (var seasonNumber in episodes.Where(episode => episode.IsMissing).Select(episode => episode.SeasonNumber).Distinct().Take(SeasonsPerPass).ToArray())
            {
                for (var attempt = 0; attempt < GrabsPerSeason; attempt++)
                {
                    if (AcquisitionSelectionLogic.ReadNextSeriesScope(episodes, seasonNumber, attempt == 0) is not { } scope)
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
                    episodes = AcquisitionSelectionLogic.Claim(episodes, scope, winner.Candidate.Parsed);
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

        internal RefreshWantedMetadataV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Wanted series learn about new episodes once a day.
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
                        JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = wanted."WorkId" AND identity."Namespace" = 'tmdb_tv'
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

            // Without a reachable provider the series keep their known episodes.
            try
            {
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                foreach (var identity in stale)
                {
                    await _metadata.ImportAsync(Context, connection, MediaType.Series, int.Parse(identity.Value, CultureInfo.InvariantCulture), CancellationToken);
                }
            }
            catch (ServiceUnavailableException)
            {
                return new UpdateResult(false);
            }

            return new UpdateResult(true);
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
        private readonly PlexServer _plex;
        private readonly GutenbergClient _gutenberg;

        internal MonitorDownloadsV1(SabnzbdClient sabnzbd, GutenbergClient gutenberg, PlexServer plex, IDataProtectionProvider protection, IOptions<StorageOptions> options, TimeProvider time)
        {
            _gutenberg = gutenberg;
            _sabnzbd = sabnzbd;
            _plex = plex;
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
                if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Gutenberg)
                {
                    foreach (var download in group.Take(DirectPerCycle))
                    {
                        await DownloadDirectAsync(download);
                    }

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
                await FailAsync(download.OperationId, DownloadLogic.InterruptedReasonKey, _time.GetUtcNow().UtcDateTime + InterruptedRetry);
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
                    await FailAsync(download.OperationId, DownloadLogic.InterruptedReasonKey, now + InterruptedRetry);
                }

                return;
            }

            if (finished.IsFailed)
            {
                var releaseFault = SabnzbdFailureKinds.IsReleaseFault(finished.FailureKind);
                await FailAsync(download.OperationId, releaseFault ? DownloadLogic.FailedReasonKey : DownloadLogic.InterruptedReasonKey, releaseFault ? now : now + InterruptedRetry);
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
                await AttentionAsync(download.OperationId, "import_path_unmapped");
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
                await AttentionAsync(download.OperationId, "import_source_unavailable");
                return;
            }

            await PlaceAsync(download, source, files, state.IsComplete);
        }

        // Shared by every client: plan, check, link or copy and assign.
        internal async Task PlaceAsync(DownloadData download, string source, IReadOnlyList<StorageFile> files, bool isComplete)
        {
            var root = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadImportRootAsync(download.WorkId, ct), CancellationToken);
            if (root is null)
            {
                await AttentionAsync(download.OperationId, "import_no_library");
                return;
            }

            // Ebooks must be what their names say.
            var placements = ImportPlacementLogic.Plan(download.MediaTypeId, download.CanonicalTitle, download.ReleaseYear, download.QualityKey, files, download.Author)
                .Where(placement => !placement.IsMedia || LibraryFileTypes.GetAssetType(placement.SourcePath) != MediaAssetType.Ebook || IsEbook(source, placement))
                .ToArray();
            if (placements.Length == 0)
            {
                if (isComplete)
                {
                    await FailAsync(download.OperationId, DownloadLogic.FailedReasonKey, _time.GetUtcNow().UtcDateTime);
                }

                return;
            }

            // Link or copy outside transactions; an existing library file is never taken over.
            // An offline library keeps the download waiting instead of writing elsewhere.
            string directory;
            try
            {
                directory = StorageFileSystem.ResolveDirectory(_options.GetConnectionPath(root.StorageProtocolTypeId, root.StorageMountId, root.SharePath), root.RelativePath);
            }
            catch (NasMountException)
            {
                await AttentionAsync(download.OperationId, "import_library_unavailable");
                return;
            }

            var imported = new List<ImportedFile>();
            var media = new List<(string RelativePath, ImportPlacementData Placement)>();
            foreach (var group in placements.GroupBy(placement => placement.Group))
            {
                var stem = Enumerable.Range(1, 99).Select(attempt => ImportPlacementLogic.Alternative(group.First().TargetStem, attempt))
                    .First(candidate => group.All(placement => !File.Exists(Path.Combine(directory, (candidate + placement.Extension).Replace('/', Path.DirectorySeparatorChar)))));
                foreach (var placement in group)
                {
                    var relativePath = stem + placement.Extension;
                    var folder = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                    Directory.CreateDirectory(Path.Combine(directory, folder));
                    var destination = Path.Combine(StorageFileSystem.ResolveDirectory(directory, folder), Path.GetFileName(relativePath));
                    var sourceFile = StorageFileSystem.ResolveFile(source, placement.SourcePath);
                    await Task.Run(() => StorageFileTransfer.HardlinkOrCopy(sourceFile, destination), CancellationToken);

                    var info = new FileInfo(destination);
                    imported.Add(new ImportedFile(relativePath, info.Length, Truncate(info.LastWriteTimeUtc)));
                    if (placement.IsMedia)
                    {
                        media.Add((relativePath, placement));
                    }
                }
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.UpsertStoredFilesAsync(new StoredFileBatchData(root.Id, Truncate(DateTime.UtcNow), JsonSerializer.Serialize(imported)), ct);
                foreach (var (relativePath, placement) in media)
                {
                    if (await ctx.ReadStoredFileIdAsync(new LibraryFilePathData(root.Id, relativePath), ct) is { } file)
                    {
                        await ctx.AssignStoredFileAsync(new StoredFileAssignmentData(file.Id, download.WorkId, null, placement.WorkUnitTypeId, placement.UnitNumber, placement.ParentUnitNumber), ct);
                    }
                }

                await ctx.CompleteDownloadAsync(new DownloadOperationParameters(download.OperationId), ct);

                // The scan analyses the new streams.
                await ctx.QueueLibraryScanAsync(new OperationCreationData(OperationKind.LibraryScan, root.Id, null), ct);
                return true;
            }, CancellationToken);

            // A connected Plex shows the title right away.
            await _plex.RefreshAsync(Context, download.MediaTypeId, CancellationToken);
        }

        private static bool IsEbook(string source, ImportPlacementData placement)
        {
            var header = new byte[128];
            using var stream = File.OpenRead(StorageFileSystem.ResolveFile(source, placement.SourcePath));
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return LibraryFileTypes.IsEbookContent(placement.Extension, header.AsSpan(0, read));
        }

        // Jularr downloads catalog books itself into staging, then imports them like any other job.
        private async Task DownloadDirectAsync(DownloadData download)
        {
            if (download.ExternalId is not { } external || !external.StartsWith(WantedAcquisition.GutenbergPrefix, StringComparison.Ordinal)
                || !int.TryParse(external[WantedAcquisition.GutenbergPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var bookId))
            {
                await FailAsync(download.OperationId, DownloadLogic.FailedReasonKey, _time.GetUtcNow().UtcDateTime);
                return;
            }

            var staging = Path.Combine(_options.DirectDownloadPath, download.OperationId.ToString(CultureInfo.InvariantCulture));
            var name = bookId.ToString(CultureInfo.InvariantCulture) + ".epub";
            var path = Path.Combine(staging, name);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(staging);
                try
                {
                    await _gutenberg.DownloadAsync(bookId, path + ".part", CancellationToken);
                    File.Move(path + ".part", path);
                }
                catch (Exception exception) when (exception is GutenbergException or IOException)
                {
                    File.Delete(path + ".part");
                    await FailAsync(download.OperationId, DownloadLogic.InterruptedReasonKey, _time.GetUtcNow().UtcDateTime + InterruptedRetry);
                    return;
                }
            }

            var file = new FileInfo(path);
            await PlaceAsync(download, staging, [new StorageFile(name, file.Length, file.LastWriteTimeUtc)], true);

            // Staging is kept only while the import waits for the Owner.
            var finished = await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsDownloadFinishedAsync(new DownloadOperationParameters(download.OperationId), ct), CancellationToken);
            if (finished)
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        private Task AttentionAsync(long operationId, string attentionKey) =>
            Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SetDownloadAttentionAsync(new DownloadAttentionData(operationId, attentionKey), ct);
                return true;
            }, CancellationToken);

        private Task FailAsync(long operationId, string reasonKey, DateTime nextSearchAt) =>
            Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.FailDownloadAsync(new DownloadFailureData(operationId, reasonKey, nextSearchAt), ct);
                return true;
            }, CancellationToken);

        private static DateTime Truncate(DateTime value) => DateTime.UnixEpoch.AddTicks((value.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 10);

        private sealed class ImportedFile
        {
            public ImportedFile(string relativePath, long sizeBytes, DateTime modifiedAt)
            {
                RelativePath = relativePath;
                SizeBytes = sizeBytes;
                ModifiedAt = modifiedAt;
            }

            public string RelativePath { get; }
            public long SizeBytes { get; }
            public DateTime ModifiedAt { get; }
        }
    }
}
