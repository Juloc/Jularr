using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Narou;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // Read-only query of the imported, eligible Narō web novel EPUBs.
    // Every matching file retains its StoredFileId and MediaAssetId across updates.
    internal sealed class NarouSyncFileData(
        long workId, long storedFileId, long storageMountId, string rootPath, string relativePath,
        long sizeBytes, string ncode) : BaseData
    {
        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; } = workId;
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; } = storedFileId;
        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; } = storageMountId;
        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootPath { get; } = rootPath;
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; } = relativePath;
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; } = sizeBytes;
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value))]
        public string Ncode { get; } = ncode;
    }

    internal sealed class NarouUpdatedFileData(long storedFileId, long oldSizeBytes, long newSizeBytes, DateTime modifiedAt) : BaseData
    {
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; } = storedFileId;
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long OldSizeBytes { get; } = oldSizeBytes;
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long NewSizeBytes { get; } = newSizeBytes;
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; } = modifiedAt;
    }

    internal sealed class NarouSyncCheckData(long workId, DateTime checkedAt, string? errorCode) : BaseData
    {
        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; } = workId;
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime CheckedAt { get; } = checkedAt;
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Value), false)]
        public string? ErrorCode { get; } = errorCode;
    }

    // Lightweight periodic post-import updater. There is no second Wanted, Download or
    // Reader implementation: this updates only an already-imported Narō-origin EPUB.
    // The EPUB is an append-only chapter document, and its existing MediaAssetId, stable
    // chapter filenames and section order preserve the Reader's anchors/bookmarks.
    public sealed class SyncNarouChaptersV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private const int PerPass = 4;
        private readonly NarouClient narou;
        private readonly StorageAccess storage;
        private readonly ILogger<SyncNarouChaptersV1> logger;

        internal SyncNarouChaptersV1(NarouClient narou, StorageAccess storage, ILogger<SyncNarouChaptersV1> logger)
        {
            this.narou = narou;
            this.storage = storage;
            this.logger = logger;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) =>
            ModuleRequirement.All("Acquisition", "Library");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) =>
            new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (files, settings) = await Context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var sql = new SqlStatement
                {
                    Statement = """
                        WITH candidates AS (
                            SELECT DISTINCT ON (work."Id")
                                work."Id" AS "WorkId",
                                file."Id" AS "StoredFileId", root."StorageMountId",
                                root."RelativePath" AS "RootPath", file."RelativePath",
                                file."SizeBytes", identity."Value" AS "Ncode"
                            FROM "WantedItems" AS wanted
                            JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                            JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = work."Id" AND identity."Namespace" = 'syosetu'
                            JOIN "MediaAssets" AS asset ON asset."WorkId" = work."Id" AND asset."WorkUnitId" IS NULL AND NOT asset."IsProtected"
                            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL
                            JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                            JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                            WHERE work."MediaTypeId" = 3 AND work."MediaSubtypeId" = 3
                              AND NOT wanted."IsPaused" AND NOT mount."IsReadOnly"
                              AND lower(right(file."RelativePath", 5)) = '.epub'
                              AND EXISTS (
                                  SELECT 1 FROM "AcquisitionConnections" AS connection
                                  WHERE connection."AcquisitionConnectionTypeId" = 9 AND connection."IsEnabled"
                              )
                            ORDER BY work."Id", file."Id"
                        )
                        SELECT candidate.*
                        FROM candidates AS candidate
                        LEFT JOIN "NarouSyncStates" AS state ON state."WorkId" = candidate."WorkId"
                        ORDER BY state."LastCheckedAt" NULLS FIRST, candidate."WorkId"
                        LIMIT 4;
                        """
                };
                return (await ctx.ReadSql.ReadListAsync<NarouSyncFileData>(sql, token),
                    await ctx.ReadDownloadStorageSettingsAsync(DownloadStorageArea.Direct, token));
            }, CancellationToken);

            foreach (var file in files.Take(PerPass))
            {
                string? errorCode = null;
                try
                {
                    await AppendAsync(file, settings.MaxJobBytes, CancellationToken);
                }
                catch (Exception error) when (error is NarouException or IOException or UnauthorizedAccessException
                    or System.IO.InvalidDataException)
                {
                    errorCode = "narou_sync_failed";
                    logger.LogWarning(error, "Narō update for file {StoredFileId} requires retry or review.", file.StoredFileId);
                }
                finally
                {
                    // Persistent oldest-first cursor: every monitored work is eventually visited.
                    await Context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        var sql = new SqlStatement
                        {
                            Statement = """
                                INSERT INTO "NarouSyncStates" ("WorkId", "LastCheckedAt", "LastErrorCode")
                                VALUES (@WorkId, @CheckedAt, @ErrorCode)
                                ON CONFLICT ("WorkId") DO UPDATE SET
                                    "LastCheckedAt" = EXCLUDED."LastCheckedAt",
                                    "LastErrorCode" = EXCLUDED."LastErrorCode";
                                """
                        };
                        sql.Parameters.Add(new NarouSyncCheckData(file.WorkId, DateTime.UtcNow, errorCode));
                        await ctx.ExecuteAsyncForAffectedRows(sql, token);
                        return true;
                    }, CancellationToken);
                }
            }
            return new UpdateResult(true);
        }

        private async Task AppendAsync(NarouSyncFileData file, long? sizeLimit, CancellationToken ct)
        {
            var mount = await Context.ExecuteBatchAsync((ctx, token) =>
                ctx.ReadStorageMountAsync(file.StorageMountId, token), ct);
            if (mount.IsReadOnly || !NarouClient.IsNcode(file.Ncode)) return;

            var library = storage.Open(mount);
            var path = StoragePath.Combine(file.RootPath, file.RelativePath);
            var entry = await library.GetAsync(path, ct);
            if (entry is null || entry.Length != file.SizeBytes) return; // changed by a scanner/person

            byte[]? update;
            await using (var original = await library.OpenReadAsync(path, ct))
            {
                update = await narou.AppendNewChaptersAsync(file.Ncode, original, sizeLimit, ct);
            }
            if (update is null) return;

            // Never mutate the working EPUB directly. The old copy remains recoverable
            // through an atomic same-mount rename until both the new file and DB commit.
            var suffix = "." + Guid.NewGuid().ToString("N");
            var staging = path + suffix + ".partial";
            var backup = path + suffix + ".previous";
            var backedUp = false;
            var newInPlace = false;
            var committed = false;
            try
            {
                await using (var output = await library.CreateAsync(staging, ct)
                    ?? throw new IOException("Cannot reserve a Narō update file."))
                {
                    await output.WriteAsync(update, ct);
                }
                if (!await library.MoveAsync(path, backup, ct))
                    throw new IOException("Original book cannot be moved to a safe backup.");
                backedUp = true;
                if (!await library.MoveAsync(staging, path, ct))
                    throw new IOException("Updated book cannot be placed.");
                newInPlace = true;
                var revised = await library.GetAsync(path, ct)
                    ?? throw new IOException("Updated book is missing after placing it.");
                if (revised.Length != update.Length)
                    throw new IOException("Updated book size changed during placement.");

                await Context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    var sql = new SqlStatement
                    {
                        Statement = """
                            UPDATE "StoredFiles"
                            SET "SizeBytes" = @NewSizeBytes, "ModifiedAt" = @ModifiedAt,
                                "LastSeenAt" = now(), "Sha1" = NULL, "ProbedAt" = NULL
                            WHERE "Id" = @StoredFileId AND "SizeBytes" = @OldSizeBytes
                              AND "MissingSince" IS NULL;
                            """
                    };
                    sql.Parameters.Add(new NarouUpdatedFileData(
                        file.StoredFileId, file.SizeBytes, revised.Length, StoredFileLogic.StoredTime(revised.ModifiedAt)));
                    if (await ctx.ExecuteAsyncForAffectedRows(sql, token) != 1)
                        throw new IOException("The stored book changed during Narō synchronization.");
                    return true;
                }, ct);
                committed = true;
                logger.LogInformation("Appended Narō chapters to existing EPUB, file {StoredFileId}.", file.StoredFileId);
            }
            finally
            {
                if (backedUp && !committed)
                {
                    // DB and file must agree. Roll back the file if this pass could not commit.
                    try
                    {
                        if (newInPlace)
                        {
                            // The new file is not registered in DB; discard it.
                            await library.DeleteFileAsync(path, CancellationToken.None);
                        }
                        await library.MoveAsync(backup, path, CancellationToken.None);
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error, "Narō file {StoredFileId} needs storage recovery; previous copy: {Backup}.",
                            file.StoredFileId, backup);
                    }
                }
                if (committed)
                {
                    try { await library.DeleteFileAsync(backup, CancellationToken.None); }
                    catch (IOException error)
                    {
                        logger.LogWarning(error, "Narō backup {Backup} needs cleanup.", backup);
                    }
                }
                try
                {
                    if (await library.ExistsAsync(staging, CancellationToken.None))
                        await library.DeleteFileAsync(staging, CancellationToken.None);
                }
                catch (IOException error)
                {
                    logger.LogWarning(error, "Incomplete Narō staging file {Staging} needs cleanup.", staging);
                }
            }
        }
    }
}
