using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/library/roots")]
    public sealed class ReadLibraryRootsV1 : AdminService<ReadLibraryRootsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LibraryRootDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadLibraryRootsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadLibraryRootsParametersV1 parameters, Type resultType) => new("storage.read");
        protected override PageRequest GetPageRequest(ReadLibraryRootsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id",
                    root."Name",
                    root."MediaTypeId",
                    root."MediaSubtypeId",
                    root."StorageMountId",
                    mount."Name" AS "StorageMountName",
                    root."RelativePath",
                    (SELECT count(*) FROM "StoredFiles" AS file WHERE file."LibraryRootId" = root."Id") AS "FileCount",
                    root."Version"
                FROM
                    "LibraryRoots" AS root
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    @StorageMountId::bigint IS NULL
                    OR root."StorageMountId" = @StorageMountId
                ORDER BY
                    root."Name",
                    root."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new LibraryRootFilterData(Parameters.StorageMountId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<LibraryRootDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/library/roots/{libraryRootId:long}")]
    public sealed class ReadLibraryRootV1 : AdminService<LibraryRootParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LibraryRootDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(LibraryRootParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(LibraryRootParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id",
                    root."Name",
                    root."MediaTypeId",
                    root."MediaSubtypeId",
                    root."StorageMountId",
                    mount."Name" AS "StorageMountName",
                    root."RelativePath",
                    (SELECT count(*) FROM "StoredFiles" AS file WHERE file."LibraryRootId" = root."Id") AS "FileCount",
                    root."Version"
                FROM
                    "LibraryRoots" AS root
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    root."Id" = @LibraryRootId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<LibraryRootDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/roots")]
    public sealed class CreateLibraryRootV1 : AdminService<CreateLibraryRootParametersV1, CreateLibraryRootDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateLibraryRootResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateLibraryRootParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CreateLibraryRootParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var data = new LibraryRootCreationData(Data.Name, Data.MediaTypeId, Data.MediaSubtypeId, Data.StorageMountId, Data.RelativePath);
            return new CreateLibraryRootResultV1(await Context.CreateLibraryRootAsync(data, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/roots/{libraryRootId:long}")]
    public sealed class RenameLibraryRootV1 : AdminService<LibraryRootParametersV1, RenameLibraryRootDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LibraryRootParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(LibraryRootParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // The media type and location stay fixed.
            await Context.RenameLibraryRootAsync(new LibraryRootRenameData(Parameters.LibraryRootId, Data.Name, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/library/roots/{libraryRootId:long}")]
    public sealed class DeleteLibraryRootV1 : AdminService<DeleteLibraryRootParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteLibraryRootParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(DeleteLibraryRootParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            // Works remain; only inventory is removed.
            await Context.DeleteLibraryRootAsync(new LibraryRootParameters(Parameters.LibraryRootId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
