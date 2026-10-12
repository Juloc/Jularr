using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    public sealed class SearchDueWantedV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private const int BatchSize = 5;
        private readonly WantedAcquisition _acquisition;
        private readonly TimeProvider _time;

        internal SearchDueWantedV1(WantedAcquisition acquisition, TimeProvider time)
        {
            _acquisition = acquisition;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var (due, indexers, clients) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await ctx.ReadDueWantedItemsAsync(new WantedDueData(now, BatchSize), ct),
                await ctx.ReadEnabledAcquisitionConnectionsAsync(AcquisitionConnectionType.Newznab, ct), await ctx.ReadEnabledAcquisitionConnectionsAsync(AcquisitionConnectionType.Sabnzbd, ct)),
                CancellationToken);
            if (indexers.Count == 0)
            {
                return new UpdateResult(due.Count == 0);
            }

            foreach (var item in due)
            {
                var target = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWantedTargetAsync(item.WantedItemId, ct), CancellationToken);
                if (target.Definition is null || WantedAcquisition.GetKind(target.MediaTypeId) is null)
                {
                    await RecordAsync(target.WantedItemId, "profile_missing", now.AddDays(1));
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

    public sealed class MonitorDownloadsV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan InterruptedRetry = TimeSpan.FromMinutes(30);
        private readonly SabnzbdClient _sabnzbd;
        private readonly IDataProtector _protector;
        private readonly StorageOptions _options;
        private readonly TimeProvider _time;

        internal MonitorDownloadsV1(SabnzbdClient sabnzbd, IDataProtectionProvider protection, IOptions<StorageOptions> options, TimeProvider time)
        {
            _sabnzbd = sabnzbd;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _options = options.Value;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var downloads = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadActiveDownloadsAsync(ct), CancellationToken);
            foreach (var group in downloads.GroupBy(download => download.AcquisitionConnectionId))
            {
                // An unreachable client keeps its downloads running.
                var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(group.Key, ct), CancellationToken);
                var client = new SabnzbdConnection(connection.BaseUrl, AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey));
                SabnzbdQueueSnapshot queue;
                SabnzbdHistorySnapshot history;
                try
                {
                    queue = await _sabnzbd.GetQueueAsync(client, CancellationToken);
                    history = await _sabnzbd.GetHistoryAsync(client, group.Select(download => download.ExternalId).ToArray(), CancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or SabnzbdException or JsonException
                    || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                foreach (var download in group)
                {
                    await TrackAsync(download, connection, queue, history);
                }
            }

            return new UpdateResult(true);
        }

        private async Task TrackAsync(DownloadData download, AcquisitionConnectionData connection, SabnzbdQueueSnapshot queue, SabnzbdHistorySnapshot history)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (queue.Jobs.FirstOrDefault(job => job.NzoId == download.ExternalId) is { } queued)
            {
                var percent = queued.Percentage is { } value ? (int)Math.Clamp(Math.Round(value), 0, 99) : (int?)null;
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.UpdateDownloadProgressAsync(new DownloadProgressData(download.OperationId, percent), ct);
                    return true;
                }, CancellationToken);
                return;
            }

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
            if (finished.IsCompleted && MapPath(finished.StoragePath, connection) is { } source)
            {
                await ImportAsync(download, source);
            }
        }

        private async Task ImportAsync(DownloadData download, string source)
        {
            // Without a writable library or readable files the download waits for the Owner.
            var root = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadImportRootAsync(download.WorkId, ct), CancellationToken);
            if (root is null || !(File.Exists(source) || Directory.Exists(source)))
            {
                return;
            }

            var placements = Place(download, source);
            if (placements.Count == 0)
            {
                await FailAsync(download.OperationId, DownloadLogic.FailedReasonKey, _time.GetUtcNow().UtcDateTime);
                return;
            }

            // Link or copy outside transactions; existing library files are kept.
            var directory = StorageFileSystem.ResolveDirectory(_options.GetConnectionPath(root.StorageProtocolTypeId, root.StorageMountId, root.SharePath), root.RelativePath);
            var files = new List<ImportedFile>();
            foreach (var placement in placements)
            {
                var destination = Path.Combine(directory, placement.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(destination))
                {
                    await Task.Run(() => StorageFileTransfer.HardlinkOrCopy(placement.SourcePath, destination), CancellationToken);
                }

                var info = new FileInfo(destination);
                files.Add(new ImportedFile(placement.RelativePath, info.Length, Truncate(info.LastWriteTimeUtc)));
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.UpsertStoredFilesAsync(new StoredFileBatchData(root.Id, Truncate(DateTime.UtcNow), JsonSerializer.Serialize(files)), ct);
                foreach (var placement in placements.Where(placement => placement.IsMedia))
                {
                    if (await ctx.ReadStoredFileIdAsync(new LibraryFilePathData(root.Id, placement.RelativePath), ct) is { } file)
                    {
                        await ctx.AssignStoredFileAsync(new StoredFileAssignmentData(file.Id, download.WorkId, null, placement.WorkUnitTypeId, placement.UnitNumber, placement.ParentUnitNumber), ct);
                    }
                }

                await ctx.CompleteDownloadAsync(new DownloadOperationParameters(download.OperationId), ct);

                // The scan analyses the new streams.
                await ctx.QueueLibraryScanAsync(new OperationCreationData(OperationKind.LibraryScan, root.Id, null), ct);
                return true;
            }, CancellationToken);
        }

        private static List<Placement> Place(DownloadData download, string source)
        {
            var videos = (File.Exists(source) ? [source] : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                .Where(path => LibraryFileTypes.GetAssetType(path) == MediaAssetType.Video && !MediaPathParser.IsSample(path))
                .Select(path => new FileInfo(path))
                .ToArray();
            var title = SafeName(download.CanonicalTitle);
            var placements = new List<Placement>();
            if (download.MediaTypeId == MediaType.Movie)
            {
                // The largest video is the feature.
                if (videos.MaxBy(video => video.Length) is { } feature)
                {
                    var name = download.ReleaseYear is { } year ? $"{title} ({year.ToString(CultureInfo.InvariantCulture)})" : title;
                    AddWithSubtitles(placements, feature, $"{name}/{name}", null, null, null);
                }

                return placements;
            }

            // Episodes keep their numbering.
            foreach (var video in videos)
            {
                if (!ReleaseParser.TryParse(Path.GetFileNameWithoutExtension(video.Name), out var parsed) || parsed.SeasonNumber is not { } season || parsed.EpisodeStart is not { } episode)
                {
                    continue;
                }

                var seasonText = season.ToString("00", CultureInfo.InvariantCulture);
                AddWithSubtitles(placements, video, $"{title}/Season {seasonText}/{title} - S{seasonText}E{episode.ToString("00", CultureInfo.InvariantCulture)}", WorkUnitType.Episode, episode,
                    season);
            }

            return placements;
        }

        private static void AddWithSubtitles(List<Placement> placements, FileInfo video, string stem, WorkUnitType? unitType, decimal? unitNumber, decimal? parentUnitNumber)
        {
            var relativePath = stem + video.Extension.ToLowerInvariant();
            if (!IsStorable(relativePath))
            {
                return;
            }

            placements.Add(new Placement(video.FullName, relativePath, true, unitType, unitNumber, parentUnitNumber));

            // Subtitles named like the video follow it.
            var videoStem = Path.GetFileNameWithoutExtension(video.Name);
            foreach (var subtitle in video.Directory!.EnumerateFiles(videoStem + ".*").Where(file => LibraryFileTypes.IsSubtitle(file.Name)))
            {
                var subtitlePath = stem + subtitle.Name[videoStem.Length..].ToLowerInvariant();
                if (IsStorable(subtitlePath))
                {
                    placements.Add(new Placement(subtitle.FullName, subtitlePath, false, null, null, null));
                }
            }
        }

        private static string? MapPath(string? storagePath, AcquisitionConnectionData connection)
        {
            // Client paths are translated through the connection's mapping.
            if (string.IsNullOrWhiteSpace(storagePath))
            {
                return null;
            }

            if (connection.RemotePath is not { } remote || connection.LocalPath is not { } local)
            {
                return storagePath;
            }

            var normalized = storagePath.Replace('\\', '/');
            var prefix = remote.Replace('\\', '/').TrimEnd('/');
            if (!normalized.Equals(prefix, StringComparison.Ordinal) && !normalized.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return storagePath;
            }

            var rest = normalized[prefix.Length..].TrimStart('/');
            return rest.Length == 0 ? local : Path.Combine(local, rest.Replace('/', Path.DirectorySeparatorChar));
        }

        private Task FailAsync(long operationId, string reasonKey, DateTime nextSearchAt) =>
            Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.FailDownloadAsync(new DownloadFailureData(operationId, reasonKey, nextSearchAt), ct);
                return true;
            }, CancellationToken);

        private static string SafeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
            var cleaned = new string(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
            return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ') is { Length: > 0 } name ? name : "Untitled";
        }

        private static bool IsStorable(string relativePath)
        {
            try
            {
                StoredFileFields.RelativePath.Validate(relativePath, nameof(ImportedFile.RelativePath));
                return true;
            }
            catch (DataValidationException)
            {
                return false;
            }
        }

        private static DateTime Truncate(DateTime value) => DateTime.UnixEpoch.AddTicks((value.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 10);

        private sealed record Placement(string SourcePath, string RelativePath, bool IsMedia, WorkUnitType? WorkUnitTypeId, decimal? UnitNumber, decimal? ParentUnitNumber);

        private sealed record ImportedFile(string RelativePath, long SizeBytes, DateTime ModifiedAt);
    }
}
