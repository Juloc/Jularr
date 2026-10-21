using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/volumes")]
    public sealed class UploadVolumeV1 : AdminService<WorkParametersV1, UploadVolumeDataV1>
    {
        private readonly StorageAccess _storage;
        private readonly WorkSignals _signals;

        internal UploadVolumeV1(StorageAccess storage, WorkSignals signals)
        {
            _storage = storage;
            _signals = signals;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "Uploads");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Manga volumes only, into the library the import would choose.
            var target = await Context.ExecuteBatchAsync((ctx, ct) => ReadTargetAsync(ctx, ct), CancellationToken);
            if (target.MediaTypeId != MediaType.Book || target.MediaSubtypeId != MediaSubtype.Manga)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "invalid_transition");
            }

            if (target is not { LibraryRootId: { } rootId, StorageMountId: { } mountId, RelativePath: { } rootPath })
            {
                throw new ServiceUnavailableException("import_no_library");
            }

            var file = Data.File ?? throw new InvalidOperationException("The volume file is validated as required.");
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var relativePath = LibraryNaming.Volume(target.CanonicalTitle, Data.VolumeNumber) + extension;
            if (!LibraryNaming.IsStorable(relativePath))
            {
                throw new DataValidationException(nameof(UploadVolumeDataV1.File), "invalid_value");
            }

            // The file must be what its name says.
            var header = new byte[128];
            await using (var check = file.OpenRead())
            {
                var read = await check.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, CancellationToken);
                if (!LibraryFileTypes.IsDocumentContent(extension, header.AsSpan(0, read)))
                {
                    throw new DataValidationException(nameof(UploadVolumeDataV1.File), "invalid_file_type");
                }
            }

            // An existing volume stays.
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(mountId, ct), CancellationToken);
            var library = _storage.Open(mount);
            if (await library.GetAsync(rootPath, CancellationToken) is not { IsDirectory: true })
            {
                throw new NasMountException("storage_unavailable");
            }

            await using var content = file.OpenRead();
            var (length, modifiedAt) = await LibraryFileWriter.WriteAsync(library, rootPath, relativePath, content, CancellationToken)
                ?? throw new DataValidationException(nameof(UploadVolumeDataV1.File), "duplicate_value");

            // Assigned at once; the scan reads it like an imported file.
            var files = JsonSerializer.Serialize(new[] { new { RelativePath = relativePath, SizeBytes = length, ModifiedAt = modifiedAt, Revision = 1 } });
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.UpsertStoredFilesAsync(new StoredFileBatchData(rootId, modifiedAt, files), ct);
                var file = await ctx.ReadStoredFileIdAsync(new LibraryFilePathData(rootId, relativePath), ct) ?? throw new KeyNotFoundException("The uploaded file was not found.");
                await ctx.AssignStoredFileAsync(new StoredFileAssignmentData(file.Id, Parameters.WorkId, null, Data.VolumeNumber is null ? null : WorkUnitType.Volume,
                    Data.VolumeNumber, null, null, true), ct);
                await ctx.QueueLibraryScanAsync(new OperationCreationData(OperationKind.LibraryScan, rootId, null), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _signals.Scans.Notify();

        // The Work and the first writable library of its type and effective subtype.
        private Task<UploadTargetData> ReadTargetAsync(LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."CanonicalTitle",
                    work."MediaTypeId",
                    kind."MediaSubtypeId",
                    root."Id" AS "LibraryRootId",
                    root."StorageMountId",
                    root."RelativePath"
                FROM
                    "Works" AS work
                    LEFT JOIN "WorkClasses" AS class ON class."WorkId" = work."Id"
                    CROSS JOIN LATERAL (
                        SELECT
                            CASE WHEN class."WorkId" IS NULL THEN work."MediaSubtypeId" ELSE class."MediaSubtypeId" END AS "MediaSubtypeId"
                    ) AS kind
                    LEFT JOIN LATERAL (
                        SELECT
                            candidate."Id",
                            mount."Id" AS "StorageMountId",
                            candidate."RelativePath"
                        FROM
                            "LibraryRoots" AS candidate
                            JOIN "StorageMounts" AS mount ON mount."Id" = candidate."StorageMountId"
                        WHERE
                            candidate."MediaTypeId" = work."MediaTypeId"
                            AND candidate."MediaSubtypeId" IS NOT DISTINCT FROM kind."MediaSubtypeId"
                            AND NOT mount."IsReadOnly"
                        ORDER BY
                            candidate."Id"
                        LIMIT 1
                    ) AS root ON TRUE
                WHERE
                    work."Id" = @WorkId;
                """;
            sql.Parameters.Add(Parameters);
            return ctx.ReadSql.ReadRequiredAsync<UploadTargetData>(sql, ct);
        }
    }
}
