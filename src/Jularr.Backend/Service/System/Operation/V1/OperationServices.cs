using System.Text.Json;
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
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

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
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LibraryScanScheduleParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(LibraryScanScheduleParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Changes made outside Jularr appear after the next scan.
            return new UpdateResult(await Context.QueueDueLibraryScansAsync(CancellationToken) > 0);
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

    public sealed class ClaimOperationV1 : SystemService<OperationQueueParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ClaimOperationResultV1>()];
        protected override ModuleRequirement GetInstanceModules(OperationQueueParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(OperationQueueParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var claimed = await Context.ClaimOperationAsync(CancellationToken);
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
        private const int ExternalLookupsPerScan = 16;
        private readonly StorageAccess _storage;
        private readonly MediaProbeClient _probe;
        private readonly GameRomIdentification _roms;
        private readonly MusicTagIdentification _music;
        private readonly WorkMetadata _metadata;

        internal ExecuteLibraryScanV1(StorageAccess storage, MediaProbeClient probe, GameRomIdentification roms, MusicTagIdentification music, WorkMetadata metadata)
        {
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
            if (root.MediaTypeId is MediaType.Movie or MediaType.Series)
            {
                await AssignCatalogFoldersAsync(root.Id, root.MediaTypeId, storage, directory);
            }

            await Context.ExecuteBatchAsync((ctx, ct) => ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, processed, OperationStatusType.Completed), ct), CancellationToken);
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
                            TextLimit.Cut(stream.Codec, 64), TextLimit.Cut(stream.Language, 16), TextLimit.Cut(stream.Title, 256), stream.Width, stream.Height, stream.Channels, stream.IsDefault, stream.IsForced)));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or Integrations.Data.Nas.NasMountException)
                    {
                    }

                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.SaveStoredFileProbeAsync(new StoredFileProbeData(file.Id, file.SizeBytes, file.ModifiedAt, probedAt, container, durationMs, bitRate, tracks), ct);
                        return true;
                    }, CancellationToken);
                    afterId = file.Id;
                }
            }
        }

        private async Task AssignKnownFoldersAsync(long libraryRootId, MediaType mediaType, StorageFiles storage, string directory)
        {
            var nfoFolders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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
                    var hasNfo = false;
                    if (mediaType == MediaType.Series)
                    {
                        var parent = file.RelativePath.Split('/')[0];
                        if (!nfoFolders.TryGetValue(parent, out hasNfo))
                        {
                            hasNfo = await storage.GetAsync(StoragePath.Combine(directory, parent + "/tvshow.nfo"), CancellationToken) is not null;
                            if (nfoFolders.Count >= 10_000)
                            {
                                nfoFolders.Clear();
                            }

                            nfoFolders[parent] = hasNfo;
                        }
                    }

                    if (path.WorkUnitTypeId is null || hasNfo || mediaType == MediaType.Series && MediaPathParser.ProviderIds(file.RelativePath, mediaType).Ids.Count > 0)
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

        private async Task AssignCatalogFoldersAsync(long libraryRootId, MediaType mediaType, StorageFiles storage, string directory)
        {
            var matches = new Dictionary<(string Title, int? Year, string Ids), long?>();
            TmdbConnection? provider = null;
            var providerUnavailable = false;
            var externalLookups = 0;
            var nfoFolders = new Dictionary<string, (IReadOnlyDictionary<string, string> Ids, bool Conflicted)>(StringComparer.OrdinalIgnoreCase);
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
                    return;
                }

                foreach (var file in files)
                {
                    afterId = file.Id;
                    var parsed = MediaPathParser.Parse(file.RelativePath, mediaType);
                    if (parsed.Title.Length > 500 || (mediaType == MediaType.Series && parsed.WorkUnitTypeId != WorkUnitType.Episode))
                    {
                        continue;
                    }

                    var (pathIds, conflicted) = MediaPathParser.ProviderIds(file.RelativePath, mediaType);
                    var ids = new Dictionary<string, string>(pathIds, StringComparer.OrdinalIgnoreCase);
                    if (file.RelativePath.Contains('/'))
                    {
                        var folder = file.RelativePath.Split('/')[0];
                        if (!nfoFolders.TryGetValue(folder, out var nfo))
                        {
                            nfo = await ReadFolderNfoAsync(storage, directory, folder, mediaType);
                            if (nfoFolders.Count >= 10_000)
                            {
                                nfoFolders.Clear();
                            }

                            nfoFolders[folder] = nfo;
                        }

                        conflicted |= nfo.Conflicted;
                        foreach (var (source, value) in nfo.Ids)
                        {
                            if (ids.TryGetValue(source, out var existing) && !existing.Equals(value, StringComparison.OrdinalIgnoreCase))
                            {
                                conflicted = true;
                                break;
                            }

                            ids[source] = value;
                        }
                    }

                    if (conflicted)
                    {
                        continue;
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

                        // A partial identity match may belong to an existing Work. Do not create a duplicate catalog entry.
                        var hasKnownIdentity = false;
                        if (workId is null && ids.Count > 0)
                        {
                            var owners = await Context.ExecuteBatchAsync((ctx, ct) =>
                            {
                                var sql = new SqlStatement();
                                sql.Statement =
                                    """
                                    SELECT DISTINCT
                                        identity."WorkId" AS "Id"
                                    FROM
                                        jsonb_to_recordset(@ProviderIds::jsonb) AS expected("Namespace" text, "Value" text)
                                        JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = expected."Namespace"
                                            AND identity."Value" = expected."Value"
                                    LIMIT 1;
                                    """;
                                sql.Parameters.Add(new WorkFolderMatchData(mediaType, parsed.Title, parsed.Year, supplied));
                                return ctx.ReadSql.ReadListAsync<WorkIdData>(sql, ct);
                            }, CancellationToken);
                            hasKnownIdentity = owners.Count > 0;
                        }

                        // An unknown provider identity is never resolved by title alone.
                        if (workId is null && !hasKnownIdentity && ids.Count > 0 && !providerUnavailable && externalLookups < ExternalLookupsPerScan
                            && ids.Keys.All(name => name is "imdb" or "tvdb" or "tmdb_movie" or "tmdb_tv")
                            && (mediaType == MediaType.Series || !ids.ContainsKey("tvdb")))
                        {
                            externalLookups++;
                            try
                            {
                                provider ??= await _metadata.ConnectAsync(Context, CancellationToken);
                                int? tmdbId = null;
                                var verified = true;
                                foreach (var (source, value) in ids)
                                {
                                    int? resolved = source switch
                                    {
                                        "tmdb_movie" when mediaType == MediaType.Movie => int.TryParse(value, out var movieId) && movieId > 0 ? movieId : null,
                                        "tmdb_tv" when mediaType == MediaType.Series => int.TryParse(value, out var seriesId) && seriesId > 0 ? seriesId : null,
                                        "imdb" or "tvdb" => await _metadata.FindExternalAsync(provider, mediaType, source, value, CancellationToken),
                                        _ => null
                                    };
                                    if (resolved is null || tmdbId is not null && tmdbId != resolved)
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
                                    }
                                }
                            }
                            catch (ServiceUnavailableException)
                            {
                                providerUnavailable = true;
                            }
                            catch (KeyNotFoundException)
                            {
                                // Unresolved IDs remain available for manual identification.
                            }
                        }

                        if (matches.Count < 10000)
                        {
                            matches[key] = workId;
                        }
                    }

                    if (workId is null)
                    {
                        continue;
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

        private async Task<(IReadOnlyDictionary<string, string> Ids, bool Conflicted)> ReadFolderNfoAsync(StorageFiles storage, string directory, string folder, MediaType mediaType)
        {
            const int maxBytes = 65_536;
            var name = mediaType == MediaType.Series ? "tvshow.nfo" : "movie.nfo";
            var path = StoragePath.Combine(directory, folder + "/" + name);
            var info = await storage.GetAsync(path, CancellationToken);
            if (info is null)
            {
                return (new Dictionary<string, string>(), false);
            }

            if (info.IsDirectory || info.Length > maxBytes)
            {
                return (new Dictionary<string, string>(), true);
            }

            await using var stream = await storage.OpenReadAsync(path, CancellationToken);
            var data = new byte[maxBytes + 1];
            var read = 0;
            while (read < data.Length)
            {
                var length = await stream.ReadAsync(data.AsMemory(read), CancellationToken);
                if (length == 0)
                {
                    break;
                }

                read += length;
            }

            return read > maxBytes
                ? (new Dictionary<string, string>(), true)
                : MediaPathParser.ParseNfo(Encoding.UTF8.GetString(data, 0, read), mediaType);
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
            bool IsDefault, bool IsForced);
    }
}
