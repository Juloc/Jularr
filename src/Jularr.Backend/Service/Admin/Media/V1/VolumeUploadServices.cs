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
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/volumes")]
    public sealed class UploadVolumeV1 : AdminService<WorkParametersV1, UploadVolumeDataV1>
    {
        private readonly StorageOptions _storage;

        internal UploadVolumeV1(IOptions<StorageOptions> storage)
        {
            _storage = storage.Value;
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

            if (target is not { LibraryRootId: { } rootId, StorageMountId: { } mountId, StorageProtocolTypeId: { } protocol, SharePath: { } share, RelativePath: { } rootPath })
            {
                throw new ServiceUnavailableException("import_no_library");
            }

            var extension = Path.GetExtension(Data.File.FileName).ToLowerInvariant();
            var relativePath = LibraryNaming.Volume(target.CanonicalTitle, Data.VolumeNumber) + extension;
            if (!LibraryNaming.IsStorable(relativePath))
            {
                throw new DataValidationException(nameof(UploadVolumeDataV1.File), "invalid_value");
            }

            // The file must be what its name says.
            var header = new byte[128];
            await using (var check = Data.File.OpenRead())
            {
                var read = await check.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, CancellationToken);
                if (!LibraryFileTypes.IsDocumentContent(extension, header.AsSpan(0, read)))
                {
                    throw new DataValidationException(nameof(UploadVolumeDataV1.File), "invalid_file_type");
                }
            }

            // Written aside under its own name, then moved in; an existing volume stays.
            var directory = StorageFileSystem.ResolveDirectory(_storage.GetConnectionPath(protocol, mountId, share), rootPath);
            var folder = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            Directory.CreateDirectory(Path.Combine(directory, folder));
            var path = Path.Combine(StorageFileSystem.ResolveDirectory(directory, folder), Path.GetFileName(relativePath));
            if (File.Exists(path))
            {
                throw new DataValidationException(nameof(UploadVolumeDataV1.File), "duplicate_value");
            }

            var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var source = Data.File.OpenRead())
                await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write))
                {
                    await source.CopyToAsync(stream, CancellationToken);
                }

                File.Move(partial, path, false);
            }
            catch (IOException) when (File.Exists(path))
            {
                throw new DataValidationException(nameof(UploadVolumeDataV1.File), "duplicate_value");
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }

            // Assigned at once; the scan reads it like an imported file.
            var info = new FileInfo(path);
            var modifiedAt = DateTime.UnixEpoch.AddTicks((info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) / 10 * 10);
            var files = JsonSerializer.Serialize(new[] { new { RelativePath = relativePath, SizeBytes = info.Length, ModifiedAt = modifiedAt, Revision = 1 } });
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.UpsertStoredFilesAsync(new StoredFileBatchData(rootId, modifiedAt, files), ct);
                var file = await ctx.ReadStoredFileIdAsync(new LibraryFilePathData(rootId, relativePath), ct) ?? throw new KeyNotFoundException("The uploaded file was not found.");
                await ctx.AssignStoredFileAsync(new StoredFileAssignmentData(file.Id, Parameters.WorkId, null, Data.VolumeNumber is null ? null : WorkUnitType.Volume, Data.VolumeNumber, null, null), ct);
                await ctx.QueueLibraryScanAsync(new OperationCreationData(OperationKind.LibraryScan, rootId, null), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

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
                    root."StorageProtocolTypeId",
                    root."SharePath",
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
                            mount."StorageProtocolTypeId",
                            mount."SharePath",
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
