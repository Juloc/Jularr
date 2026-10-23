using System.Text.Json;
using System.Diagnostics;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.System.Operation.V1
{
    public sealed class RequeueOperationsV1 : SystemService<OperationQueueParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationQueueParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(OperationQueueParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.RequeueInterruptedOperationsAsync(CancellationToken);
            return new UpdateResult(true);
        }
    }

    public sealed class ScheduleLibraryScansV1 : SystemService<LibraryScanScheduleParametersV1, NoData>
    {
        private readonly WorkSignals _signals;
        private bool _isQueued;

        internal ScheduleLibraryScansV1(WorkSignals signals)
        {
            _signals = signals;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LibraryScanScheduleParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(LibraryScanScheduleParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Changes made outside Jularr appear after the next scan.
            _isQueued = await Context.QueueDueLibraryScansAsync(CancellationToken) > 0;
            return new UpdateResult(_isQueued);
        }

        protected override void OnCommitted()
        {
            if (_isQueued) _signals.Scans.Notify();
        }
    }

    // Activity keeps finished work and the media history as long and as much as the Admin set.
    public sealed class PruneOperationsV1 : SystemService<PruneOperationsParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PruneOperationsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PruneOperationsParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var operations = await Context.PruneFinishedOperationsAsync(CancellationToken);
            var events = await Context.PruneActivityEventsAsync(CancellationToken);
            return new UpdateResult(operations + events > 0);
        }
    }

    public sealed class ClaimOperationV1 : SystemService<ClaimOperationParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ClaimOperationResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ClaimOperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ClaimOperationParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Only scans, backups and benchmarks are queued work.
            if (Parameters.OperationTypeId is not (OperationKind.LibraryScan or OperationKind.Backup or OperationKind.TranscodeBenchmark))
            {
                throw new DataValidationException(nameof(ClaimOperationParametersV1.OperationTypeId), "invalid_value");
            }

            var claimed = await Context.ClaimOperationAsync(new OperationClaimData(Parameters.OperationTypeId), CancellationToken);
            return new ClaimOperationResultV1(claimed?.Id, claimed?.OperationTypeId);
        }
    }

    public sealed class FailOperationV1 : SystemService<OperationParametersV1, FailOperationDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(OperationParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Deleted roots take their operations with them.
            var updated = await Context.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, 0, OperationStatusType.Failed, Data.ErrorKey), CancellationToken);
            return new UpdateResult(updated == 1);
        }
    }

    public sealed class ExecuteLibraryScanV1 : SystemService<OperationParametersV1, NoData>
    {
        private const int BatchSize = 500;
        private const int CandidateBatchSize = 100;
        // New catalog titles are read from the provider for as long as this budget lasts; a scan that stops on it queues the next one.
        internal static TimeSpan LookupBudget { get; set; } = TimeSpan.FromMinutes(8);
        private readonly StorageAccess _storage;
        private readonly MediaProbeClient _probe;
        private readonly GameRomIdentification _roms;
        private readonly MusicTagIdentification _music;
        private readonly WorkMetadata _metadata;
        private readonly ILogger<ExecuteLibraryScanV1> _logger;

        internal ExecuteLibraryScanV1(StorageAccess storage, MediaProbeClient probe, GameRomIdentification roms, MusicTagIdentification music, WorkMetadata metadata, ILogger<ExecuteLibraryScanV1> logger)
        {
            _logger = logger;
            _storage = storage;
            _probe = probe;
            _roms = roms;
            _music = music;
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(OperationParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var seenAt = StoredFileLogic.StoredTime(DateTime.UtcNow);
            var root = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        root."Id",
                        root."MediaTypeId",
                        root."MediaSubtypeId",
                        root."StorageMountId",
                        root."RelativePath"
                    FROM
                        "Operations" AS operation
                        JOIN "LibraryRoots" AS root ON root."Id" = operation."LibraryRootId"
                        JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                    WHERE
                        operation."Id" = @OperationId
                        AND operation."OperationStatusTypeId" = 2;
                    """;
                sql.Parameters.Add(Parameters);
                return ctx.ReadSql.ReadRequiredAsync<LibraryRootLocationData>(sql, ct);
            }, CancellationToken);

            // Record health; unreachable storage never marks media missing.
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(root.StorageMountId, ct), CancellationToken);
            var probe = await _storage.ProbeAsync(mount, CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveStorageMountProbeAsync(probe, ct);
                return true;
            }, CancellationToken);
            var storage = _storage.Open(mount);
            var directory = StoragePath.Check(root.RelativePath);
            var processed = 0L;
            var batch = new List<ScannedFile>(BatchSize);
            var state = new StorageScanState();
            await foreach (var file in storage.EnumerateFilesAsync(directory, LibraryFileTypes.GetScanExtensions(root.MediaTypeId, root.MediaSubtypeId), state, CancellationToken))
            {
                if (MediaPathParser.IsSample(file.RelativePath) || !IsStorablePath(file.RelativePath))
                {
                    continue;
                }

                var facts = ReleaseFacts.OfPath(file.RelativePath, root.MediaTypeId == MediaType.Movie);
                batch.Add(new ScannedFile(file.RelativePath, file.SizeBytes, file.ModifiedAt, facts?.ReleaseName, facts?.ReleaseGroup, facts?.QualityKey, facts?.Edition, facts?.Revision ?? 1));
                if (batch.Count == BatchSize)
                {
                    processed += await SaveBatchAsync(root.Id, seenAt, batch, processed);
                }
            }

            processed += await SaveBatchAsync(root.Id, seenAt, batch, processed);

            // Only a complete scan may mark files missing.
            if (!state.IsComplete)
            {
                await Context.ExecuteBatchAsync((ctx, ct) => ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, processed, OperationStatusType.Failed, "scan_incomplete"), ct),
                    CancellationToken);
                return new UpdateResult(false);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.CompleteStoredFileScanAsync(new StoredFileBatchData(root.Id, seenAt, "[]"), ct);
                return true;
            }, CancellationToken);

            // Analyse new and changed media.
            if (_probe.IsAvailable)
            {
                await ProbeAsync(root.Id, storage, directory);
            }

            // The pages of new and changed comic archives.
            await CountPagesAsync(root.Id, storage, directory);

            // Verified game dumps name their game.
            if (root.MediaTypeId == MediaType.Game)
            {
                await _roms.IdentifyAsync(Context, root.Id, storage, directory, CancellationToken);
            }

            // Albums tagged with their MusicBrainz release group name their album.
            if (root.MediaTypeId == MediaType.Music)
            {
                await _music.IdentifyAsync(Context, root.Id, storage, directory, CancellationToken);
            }

            // Folders already identified keep growing.
            if (root.MediaTypeId is not (MediaType.Movie or MediaType.Game))
            {
                await AssignKnownFoldersAsync(root.Id, root.MediaTypeId, storage, directory);
            }

            // Link completely new folders to their catalog identity without moving files.
            var unfinished = root.MediaTypeId is MediaType.Movie or MediaType.Series
                && await AssignCatalogFoldersAsync(root.Id, root.MediaTypeId, storage, directory);

            await Context.ExecuteBatchAsync((ctx, ct) => ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, processed, OperationStatusType.Completed), ct), CancellationToken);
            if (unfinished)
            {
                // Titles still waiting for their catalog entry are taken by the next scan, which starts once this one is completed.
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.QueueLibraryScanAsync(new OperationCreationData(OperationKind.LibraryScan, root.Id, null), ct);
                    return true;
                }, CancellationToken);
            }

            return new UpdateResult(true);
        }

        private async Task<int> SaveBatchAsync(long libraryRootId, DateTime seenAt, List<ScannedFile> batch, long processed)
        {
            if (batch.Count == 0)
            {
                return 0;
            }

            var data = new StoredFileBatchData(libraryRootId, seenAt, JsonSerializer.Serialize(batch));
            var count = batch.Count;
            batch.Clear();
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.UpsertStoredFilesAsync(data, ct);
                return await ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, processed + count, OperationStatusType.Running), ct);
            }, CancellationToken);
            return count;
        }

        private async Task CountPagesAsync(long libraryRootId, StorageFiles storage, string directory)
        {
            for (var afterId = 0L; ;)
            {
                var files = await Context.ExecuteBatchAsync((ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT
                            file."Id",
                            file."RelativePath",
                            file."SizeBytes",
                            file."ModifiedAt",
                            NULL::bigint AS "WorkId"
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."LibraryRootId" = @LibraryRootId
                            AND file."Id" > @AfterId
                            AND file."PagesCheckedAt" IS NULL
                            AND file."MissingSince" IS NULL
                            AND file."RelativePath" ~* '\.cbz$'
                        ORDER BY
                            file."Id"
                        LIMIT 50;
                        """;
                    sql.Parameters.Add(new StoredFileCursorData(libraryRootId, afterId));
                    return ctx.ReadSql.ReadListAsync<StoredFileCandidateData>(sql, ct);
                }, CancellationToken);
                if (files.Count == 0)
                {
                    return;
                }

                foreach (var file in files)
                {
                    // An unreadable archive is recorded without pages, so it is not tried at every scan.
                    int? pages = null;
                    try
                    {
                        await using var input = await storage.OpenReadAsync(StoragePath.Combine(directory, file.RelativePath), CancellationToken);
                        pages = Logic.Reading.PageCounter.Count(input, file.RelativePath);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException or Integrations.Data.Nas.NasMountException)
                    {
                    }

                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.SaveStoredFilePagesAsync(new StoredFilePagesData(file.Id, file.SizeBytes, file.ModifiedAt, pages), ct);
                        return true;
                    }, CancellationToken);
                    afterId = file.Id;
                }
            }
        }

        private async Task ProbeAsync(long libraryRootId, StorageFiles storage, string directory)
        {
            for (var afterId = 0L; ;)
            {
                var files = await Context.ExecuteBatchAsync((ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT
                            file."Id",
                            file."RelativePath",
                            file."SizeBytes",
                            file."ModifiedAt",
                            NULL::bigint AS "WorkId"
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."LibraryRootId" = @LibraryRootId
                            AND file."Id" > @AfterId
                            AND file."ProbedAt" IS NULL
                            AND file."MissingSince" IS NULL
                            AND file."RelativePath" ~* '\.(mkv|mp4|m4v|avi|mov|wmv|ts|m2ts|webm|mpg|mpeg|mp3|flac|m4a|m4b|ogg|opus|aac|wav|wv)$'
                        ORDER BY
                            file."Id"
                        LIMIT 50;
                        """;
                    sql.Parameters.Add(new StoredFileCursorData(libraryRootId, afterId));
                    return ctx.ReadSql.ReadListAsync<StoredFileCandidateData>(sql, ct);
                }, CancellationToken);
                if (files.Count == 0)
                {
                    return;
                }

                foreach (var file in files)
                {
                    // Unreadable files are recorded without streams.
                    var probedAt = DateTime.UtcNow;
                    var tracks = "[]";
                    var chapters = "[]";
                    var sidecarMarkers = "[]";
                    string? container = null;
                    long? durationMs = null;
                    long? bitRate = null;
                    try
                    {
                        using var input = _storage.Input(storage, StoragePath.Combine(directory, file.RelativePath));
                        var probe = await _probe.ProbeAsync(input.Location, CancellationToken);
                        container = probe.Container is { Length: <= 64 } name ? name : null;
                        durationMs = probe.DurationMs is >= 0 ? probe.DurationMs : null;
                        bitRate = probe.BitRate is >= 0 ? probe.BitRate : null;
                        tracks = JsonSerializer.Serialize(probe.Streams.Select(stream => new ProbedTrack(stream.Index,
                            stream.CodecType switch { "video" => MediaTrackType.Video, "audio" => MediaTrackType.Audio, _ => MediaTrackType.Subtitle },
                            TextLimit.Cut(stream.Codec, 64), TextLimit.Cut(stream.Language, 16), TextLimit.Cut(stream.Title, 256), stream.Width, stream.Height, stream.Channels, stream.IsDefault, stream.IsForced, stream.BitDepth, TextLimit.Cut(stream.DynamicRange, 16), stream.Is3D)));
                        chapters = JsonSerializer.Serialize(probe.Chapters.Select((chapter, index) => new MediaChapterRow(index, chapter.StartMs, chapter.EndMs, TextLimit.Cut(chapter.Title, 256))));

                        // A sidecar file beside the video names markers too.
                        sidecarMarkers = await ReadSidecarMarkersAsync(storage, directory, file.RelativePath, durationMs);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or Integrations.Data.Nas.NasMountException)
                    {
                    }

                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.SaveStoredFileProbeAsync(new StoredFileProbeData(file.Id, file.SizeBytes, file.ModifiedAt, probedAt, container, durationMs, bitRate, tracks, chapters, sidecarMarkers), ct);
                        return true;
                    }, CancellationToken);
                    afterId = file.Id;
                }
            }
        }

        private async Task AssignKnownFoldersAsync(long libraryRootId, MediaType mediaType, StorageFiles storage, string directory)
        {
            var hintFolders = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var afterId = 0L; ;)
            {
                // Top folders linked to exactly one Work.
                var files = await Context.ExecuteBatchAsync((ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT
                            file."Id",
                            file."RelativePath",
                            file."SizeBytes",
                            file."ModifiedAt",
                            min(asset."WorkId") AS "WorkId"
                        FROM
                            "StoredFiles" AS file
                            JOIN "StoredFiles" AS sibling ON sibling."LibraryRootId" = file."LibraryRootId"
                                AND sibling."MediaAssetId" IS NOT NULL
                                AND split_part(sibling."RelativePath", '/', 1) = split_part(file."RelativePath", '/', 1)
                            JOIN "MediaAssets" AS asset ON asset."Id" = sibling."MediaAssetId"
                        WHERE
                            file."LibraryRootId" = @LibraryRootId
                            AND file."Id" > @AfterId
                            AND file."MediaAssetId" IS NULL
                            AND file."MissingSince" IS NULL
                            AND strpos(file."RelativePath", '/') > 0
                            AND NOT file."IsSubtitle"
                        GROUP BY
                            file."Id",
                            file."RelativePath",
                            file."SizeBytes",
                            file."ModifiedAt"
                        HAVING
                            count(DISTINCT asset."WorkId") = 1
                        ORDER BY
                            file."Id"
                        LIMIT 100;
                        """;
                    sql.Parameters.Add(new StoredFileCursorData(libraryRootId, afterId));
                    return ctx.ReadSql.ReadListAsync<StoredFileCandidateData>(sql, ct);
                }, CancellationToken);
                if (files.Count == 0)
                {
                    return;
                }

                foreach (var file in files)
                {
                    // Unclear structure stays for manual review.
                    afterId = file.Id;
                    var path = MediaPathParser.Parse(file.RelativePath, mediaType);
                    var hasHints = false;
                    if (mediaType == MediaType.Series)
                    {
                        var parent = file.RelativePath.Split('/')[0];
                        if (!hintFolders.TryGetValue(parent, out hasHints))
                        {
                            hasHints = await storage.GetAsync(StoragePath.Combine(directory, parent + "/tvshow.nfo"), CancellationToken) is not null
                                || await storage.GetAsync(StoragePath.Combine(directory, parent + "/.plexmatch"), CancellationToken) is not null;
                            if (hintFolders.Count >= CandidateBatchSize)
                            {
                                hintFolders.Clear();
                            }

                            hintFolders[parent] = hasHints;
                        }

                        var containing = file.RelativePath[..file.RelativePath.LastIndexOf('/')];
                        if (!hasHints && containing != parent)
                        {
                            if (!hintFolders.TryGetValue(containing, out var seasonHints))
                            {
                                seasonHints = await storage.GetAsync(StoragePath.Combine(directory, containing + "/.plexmatch"), CancellationToken) is not null;
                                if (hintFolders.Count >= CandidateBatchSize)
                                {
                                    hintFolders.Clear();
                                }

                                hintFolders[containing] = seasonHints;
                            }

                            hasHints = seasonHints;
                        }
                    }

                    if (path.WorkUnitTypeId is null || hasHints || mediaType == MediaType.Series && MediaPathParser.ProviderIds(file.RelativePath, mediaType).Ids.Count > 0)
                    {
                        // Explicit provider IDs require catalog validation, not just a matching parent folder.
                        continue;
                    }

                    var assignment = new StoredFileAssignmentData(file.Id, file.WorkId, null, path.WorkUnitTypeId, path.UnitNumber, path.ParentUnitNumber, null, false);
                    try
                    {
                        await Context.ExecuteBatchAsync((ctx, ct) => ctx.AssignStoredFileAsync(assignment, ct), CancellationToken);
                    }
                    catch (DataValidationException)
                    {
                    }
                }
            }
        }

        private async Task<bool> AssignCatalogFoldersAsync(long libraryRootId, MediaType mediaType, StorageFiles storage, string directory)
        {
            var matches = new Dictionary<(string Title, int? Year, string Ids), long?>();
            TmdbConnection? provider = null;
            var providerUnavailable = false;
            var lookups = Stopwatch.StartNew();
            var externalLookups = 0;
            var deferred = false;
            var imported = 0;
            var refreshed = new HashSet<long>();

            // Why files stay unmatched, with one example each; written to the log when the pass ends.
            var reasons = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
            void Note(string reason, string example) => reasons[reason] = reasons.TryGetValue(reason, out var seen) ? (seen.Count + 1, seen.Example) : (1, example);
            void Report()
            {
                foreach (var (reason, seen) in reasons.OrderByDescending(item => item.Value.Count))
                {
                    _logger.LogWarning("Library scan {LibraryRootId}: {Count} files stay unmatched ({Reason}), for example {Example}", libraryRootId, seen.Count, reason, seen.Example);
                }
            }

            var nfoFolders = new Dictionary<string, (IReadOnlyDictionary<string, string> Ids, bool Conflicted)>(StringComparer.Ordinal);
            var plexFolders = new Dictionary<string, PlexMatchHints>(StringComparer.Ordinal);
            var emptyHints = MediaPathParser.ParsePlexMatch("");
            for (var afterId = 0L; ;)
            {
                var files = await Context.ExecuteBatchAsync((ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement =
                        """
                        SELECT file."Id", file."RelativePath", file."SizeBytes", file."ModifiedAt", NULL::bigint AS "WorkId"
                        FROM "StoredFiles" AS file
                        WHERE file."LibraryRootId" = @LibraryRootId AND file."Id" > @AfterId
                            AND file."MediaAssetId" IS NULL AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                        ORDER BY file."Id"
                        LIMIT 100;
                        """;
                    sql.Parameters.Add(new StoredFileCursorData(libraryRootId, afterId));
                    return ctx.ReadSql.ReadListAsync<StoredFileCandidateData>(sql, ct);
                }, CancellationToken);
                if (files.Count == 0)
                {
                    Report();

                    // A scan that imported nothing gains nothing from another one.
                    return deferred && imported > 0;
                }

                foreach (var file in files)
                {
                    afterId = file.Id;
                    var parsed = MediaPathParser.Parse(file.RelativePath, mediaType);
                    var (pathIds, conflicted) = MediaPathParser.ProviderIds(file.RelativePath, mediaType);
                    var ids = pathIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
                    string? conflict = conflicted ? "the ids in the path disagree" : null;

                    if (mediaType == MediaType.Series && file.RelativePath.Contains('/'))
                    {
                        var parts = file.RelativePath.Split('/');
                        var rootFolder = parts[0];
                        if (!plexFolders.TryGetValue(rootFolder, out var rootHints))
                        {
                            rootHints = await ReadPlexMatchAsync(storage, directory, rootFolder);
                            if (plexFolders.Count >= CandidateBatchSize)
                            {
                                plexFolders.Clear();
                            }

                            plexFolders[rootFolder] = rootHints;
                        }

                        var parent = string.Join('/', parts[..^1]);
                        var seasonHints = emptyHints;
                        if (parent != rootFolder && !plexFolders.TryGetValue(parent, out seasonHints))
                        {
                            seasonHints = await ReadPlexMatchAsync(storage, directory, parent);
                            if (plexFolders.Count >= CandidateBatchSize)
                            {
                                plexFolders.Clear();
                            }

                            plexFolders[parent] = seasonHints;
                        }

                        conflicted |= rootHints.Conflicted || seasonHints.Conflicted;
                        if (rootHints.Conflicted || seasonHints.Conflicted)
                        {
                            conflict ??= ".plexmatch is unreadable or invalid";
                        }

                        foreach (var (source, value) in rootHints.Ids.Concat(seasonHints.Ids))
                        {
                            if (ids.TryGetValue(source, out var previous) && !previous.Equals(value, StringComparison.OrdinalIgnoreCase))
                            {
                                conflicted = true;
                                conflict ??= ".plexmatch names another " + source + " id than the path";
                                break;
                            }

                            ids[source] = value;
                        }

                        parsed = MediaPathParser.ApplyPlexMatch(parsed, file.RelativePath[(rootFolder.Length + 1)..], parts[^1], rootHints, seasonHints);
                    }

                    if (parsed.Title.Length > 500 || mediaType == MediaType.Series && parsed.WorkUnitTypeId != WorkUnitType.Episode)
                    {
                        Note("the name does not give season and episode", file.RelativePath);
                        continue;
                    }
                    if (file.RelativePath.Contains('/'))
                    {
                        var folder = file.RelativePath.Split('/')[0];
                        if (!nfoFolders.TryGetValue(folder, out var nfo))
                        {
                            nfo = await ReadFolderNfoAsync(storage, directory, folder, mediaType);
                            if (nfoFolders.Count >= CandidateBatchSize)
                            {
                                nfoFolders.Clear();
                            }

                            nfoFolders[folder] = nfo;
                        }

                        conflicted |= nfo.Conflicted;
                        if (nfo.Conflicted)
                        {
                            conflict ??= "the nfo file is unreadable, too large or not a " + (mediaType == MediaType.Series ? "tvshow" : "movie") + " file";
                        }

                        foreach (var (source, value) in nfo.Ids)
                        {
                            if (ids.TryGetValue(source, out var existing) && !existing.Equals(value, StringComparison.OrdinalIgnoreCase))
                            {
                                conflicted = true;
                                conflict ??= "the nfo file names another " + source + " id than the path";
                                break;
                            }

                            ids[source] = value;
                        }
                    }

                    if (conflicted)
                    {
                        Note(conflict ?? "the hints disagree", file.RelativePath);
                        continue;
                    }

                    // Only ids a catalog here can check name a title; the rest (TVmaze, AniList, ...) that a media server writes must not keep a file from it.
                    foreach (var unverifiable in ids.Keys.Where(name => name is not ("imdb" or "tvdb" or "tmdb_movie" or "tmdb_tv")).ToArray())
                    {
                        ids.Remove(unverifiable);
                    }

                    var supplied = JsonSerializer.Serialize(ids.OrderBy(item => item.Key, StringComparer.Ordinal)
                        .Select(item => new { Namespace = item.Key, Value = item.Value }));
                    var key = (parsed.Title.ToUpperInvariant(), parsed.Year, supplied);
                    if (!matches.TryGetValue(key, out var workId))
                    {
                        var candidates = await Context.ExecuteBatchAsync((ctx, ct) =>
                        {
                            var sql = new SqlStatement();
                            sql.Statement =
                                """
                                SELECT work."Id"
                                FROM "Works" AS work
                                WHERE work."MediaTypeId" = @MediaTypeId
                                    AND (
                                        (
                                            jsonb_array_length(@ProviderIds::jsonb) > 0
                                            AND NOT EXISTS (
                                                SELECT
                                                    1
                                                FROM
                                                    jsonb_to_recordset(@ProviderIds::jsonb) AS expected("Namespace" text, "Value" text)
                                                WHERE
                                                    NOT EXISTS (
                                                        SELECT
                                                            1
                                                        FROM
                                                            "WorkExternalIdentities" AS identity
                                                        WHERE
                                                            identity."WorkId" = work."Id"
                                                            AND identity."Namespace" = expected."Namespace"
                                                            AND identity."Value" = expected."Value"
                                                    )
                                            )
                                        )
                                        OR (
                                            jsonb_array_length(@ProviderIds::jsonb) = 0
                                            AND (
                                                lower(work."CanonicalTitle") = lower(@CanonicalTitle)
                                                OR EXISTS (
                                                    SELECT
                                                        1
                                                    FROM
                                                        "WorkTitles" AS title
                                                    WHERE
                                                        title."WorkId" = work."Id"
                                                        AND lower(title."Title") = lower(@CanonicalTitle)
                                                )
                                            )
                                            AND (@ReleaseYear::integer IS NULL OR work."ReleaseYear" = @ReleaseYear)
                                        )
                                    )
                                ORDER BY work."Id"
                                LIMIT 2;
                                """;
                            sql.Parameters.Add(new WorkFolderMatchData(mediaType, parsed.Title, parsed.Year, supplied));
                            return ctx.ReadSql.ReadListAsync<WorkIdData>(sql, ct);
                        }, CancellationToken);
                        workId = candidates.Count == 1 ? candidates[0].Id : null;

                        // An identity the Work does not carry completely is checked with the provider, never guessed from the part that matches or from the title:
                        // the provider names one series for every id, and importing it extends the Work that already holds one of them.
                        if (workId is null && ids.Count > 0 && !providerUnavailable
                            && (mediaType == MediaType.Series || !ids.ContainsKey("tvdb")))
                        {
                            // Every scan reads at least one title, so a long list always moves forward.
                            if (externalLookups > 0 && lookups.Elapsed >= LookupBudget)
                            {
                                deferred = true;
                                continue;
                            }

                            externalLookups++;
                            try
                            {
                                provider ??= await _metadata.ConnectAsync(Context, CancellationToken);
                                int? tmdbId = null;
                                var verified = true;
                                foreach (var (source, value) in ids)
                                {
                                    int? resolved;
                                    try
                                    {
                                        resolved = source switch
                                        {
                                            "tmdb_movie" when mediaType == MediaType.Movie => int.TryParse(value, out var movieId) && movieId > 0 ? movieId : null,
                                            "tmdb_tv" when mediaType == MediaType.Series => int.TryParse(value, out var seriesId) && seriesId > 0 ? seriesId : null,
                                            "imdb" or "tvdb" => await _metadata.FindExternalAsync(provider, mediaType, source, value, CancellationToken),
                                            _ => null
                                        };
                                    }
                                    catch (KeyNotFoundException)
                                    {
                                        resolved = null;
                                    }
                                    // An id the catalog cannot resolve (a brand new series it does not link yet) proves nothing against the others;
                                    // only ids that name different series stop the match.
                                    if (resolved is null)
                                    {
                                        continue;
                                    }

                                    if (tmdbId is not null && tmdbId != resolved)
                                    {
                                        verified = false;
                                        break;
                                    }

                                    tmdbId = resolved;
                                }

                                if (verified && tmdbId is { } id)
                                {
                                    var details = await _metadata.ReadWorkAsync(provider, mediaType, id, CancellationToken);
                                    if ((ids.GetValueOrDefault("imdb") is not { } imdb || details.ImdbId is null || details.ImdbId.Equals(imdb, StringComparison.OrdinalIgnoreCase))
                                        && (ids.GetValueOrDefault("tvdb") is not { } tvdb || details.TvdbId is null || details.TvdbId.ToString() == tvdb))
                                    {
                                        workId = await _metadata.ImportAsync(Context, provider, mediaType, null, id, CancellationToken);
                                        imported++;
                                    }
                                }
                            }
                            catch (ServiceUnavailableException)
                            {
                                providerUnavailable = true;
                            }
                            catch (Exception exception) when (exception is KeyNotFoundException or DataValidationException)
                            {
                                // Unresolved IDs and ids of two different Works remain available for manual identification.
                            }
                        }

                        if (matches.Count < 10000)
                        {
                            matches[key] = workId;
                        }
                    }

                    if (workId is null)
                    {
                        Note(providerUnavailable ? "the catalog is not available" : ids.Count == 0 ? "no id and no single Work of that title" : "no Work holds the ids " + string.Join(", ", ids.Select(item => item.Key + " " + item.Value)) + " and the catalog could not name them", file.RelativePath);
                        continue;
                    }

                    // A series that a media server sync left without named seasons and episodes gets them from the catalog once, before its files are linked.
                    if (mediaType == MediaType.Series && !providerUnavailable && refreshed.Add(workId.Value))
                    {
                        if (externalLookups > 0 && lookups.Elapsed >= LookupBudget)
                        {
                            deferred = true;
                            refreshed.Remove(workId.Value);
                        }
                        else if (await ReadMissingStructureAsync(workId.Value) is { } tmdbId)
                        {
                            externalLookups++;
                            try
                            {
                                provider ??= await _metadata.ConnectAsync(Context, CancellationToken);
                                await _metadata.ImportAsync(Context, provider, mediaType, null, tmdbId, CancellationToken);
                                imported++;
                            }
                            catch (ServiceUnavailableException)
                            {
                                providerUnavailable = true;
                            }
                            catch (Exception exception) when (exception is DataValidationException or KeyNotFoundException)
                            {
                                // The series keeps the units its files create.
                            }
                        }
                    }

                    var assignment = new StoredFileAssignmentData(file.Id, workId, null, parsed.WorkUnitTypeId, parsed.UnitNumber, parsed.ParentUnitNumber, null, false);
                    try
                    {
                        await Context.ExecuteBatchAsync((ctx, ct) => ctx.AssignStoredFileAsync(assignment, ct), CancellationToken);
                    }
                    catch (DataValidationException)
                    {
                        // Incomplete episode structure remains available for manual review.
                    }
                }
            }
        }

        // The TMDB id of a series none of whose episodes has a name yet; null when it has one or no TMDB id to read.
        private async Task<int?> ReadMissingStructureAsync(long workId)
        {
            var identity = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT identity."Namespace", identity."Value"
                    FROM "WorkExternalIdentities" AS identity
                    WHERE identity."WorkId" = @Id AND identity."Namespace" = 'tmdb_tv'
                        AND NOT EXISTS (
                            SELECT 1 FROM "WorkUnits" AS unit
                            WHERE unit."WorkId" = identity."WorkId" AND unit."WorkUnitTypeId" = 2 AND unit."Title" IS NOT NULL
                        );
                    """;
                sql.Parameters.Add(new WorkIdData(workId));
                return ctx.ReadSql.ReadOptionalAsync<WorkIdentityProbeData>(sql, ct);
            }, CancellationToken);
            return identity is not null && int.TryParse(identity.Value, out var tmdbId) && tmdbId > 0 ? tmdbId : null;
        }

        private async Task<PlexMatchHints> ReadPlexMatchAsync(StorageFiles storage, string directory, string folder)
        {
            var (text, invalid) = await ReadHintTextAsync(storage, StoragePath.Combine(directory, folder + "/.plexmatch"), 32_768);
            return invalid
                ? new PlexMatchHints(null, null, null, new Dictionary<string, string>(), new Dictionary<string, (decimal?, decimal)>(), true)
                : MediaPathParser.ParsePlexMatch(text ?? "");
        }

        private async Task<(IReadOnlyDictionary<string, string> Ids, bool Conflicted)> ReadFolderNfoAsync(StorageFiles storage, string directory, string folder, MediaType mediaType)
        {
            var name = mediaType == MediaType.Series ? "tvshow.nfo" : "movie.nfo";
            var (text, invalid) = await ReadHintTextAsync(storage, StoragePath.Combine(directory, folder + "/" + name), 65_536);
            return invalid
                ? (new Dictionary<string, string>(), true)
                : text is null ? (new Dictionary<string, string>(), false) : MediaPathParser.ParseNfo(text, mediaType);
        }

        // The markers of the video's .edl file; one that is missing, too large or unreadable names none.
        private async Task<string> ReadSidecarMarkersAsync(StorageFiles storage, string directory, string relativePath, long? durationMs)
        {
            if (MediaMarkers.SidecarPath(relativePath) is not { } sidecar)
            {
                return "[]";
            }

            try
            {
                var (text, invalid) = await ReadHintTextAsync(storage, StoragePath.Combine(directory, sidecar), MediaMarkers.MaximumSidecarBytes);
                return invalid || text is null ? "[]" : JsonSerializer.Serialize(MediaMarkers.ParseEdl(text, durationMs));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or Integrations.Data.Nas.NasMountException)
            {
                return "[]";
            }
        }

        private async Task<(string? Text, bool Invalid)> ReadHintTextAsync(StorageFiles storage, string path, int maxBytes)
        {
            var info = await storage.GetAsync(path, CancellationToken);
            if (info is null)
            {
                return (null, false);
            }

            if (info.IsDirectory || info.Length > maxBytes)
            {
                return (null, true);
            }

            await using var stream = await storage.OpenReadAsync(path, CancellationToken);
            var buffer = new byte[maxBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var length = await stream.ReadAsync(buffer.AsMemory(total), CancellationToken);
                if (length == 0)
                {
                    break;
                }

                total += length;
            }

            if (total > maxBytes)
            {
                return (null, true);
            }

            try
            {
                return (new UTF8Encoding(false, true).GetString(buffer, 0, total), false);
            }
            catch (DecoderFallbackException)
            {
                return (null, true);
            }
        }

        private static bool IsStorablePath(string relativePath)
        {
            try
            {
                StoredFileFields.RelativePath.Validate(relativePath, nameof(ScannedFile.RelativePath));
                return true;
            }
            catch (DataValidationException)
            {
                return false;
            }
        }

        private sealed record ScannedFile(string RelativePath, long SizeBytes, DateTime ModifiedAt, string? ReleaseName, string? ReleaseGroup, string? QualityKey, string? Edition, int Revision);

        private sealed record ProbedTrack(int StreamIndex, MediaTrackType MediaTrackTypeId, string? Codec, string? Language, string? Title, int? Width, int? Height, int? Channels,
            bool IsDefault, bool IsForced, int? BitDepth = null, string? DynamicRange = null, bool Is3D = false);
    }
}
