using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Operation;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/library/files")]
    public sealed class ReadLibraryFilesV1 : AdminService<ReadLibraryFilesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<LibraryFileDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadLibraryFilesParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadLibraryFilesParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadLibraryFilesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Subtitles follow their media file.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    file."LibraryRootId",
                    root."Name" AS "LibraryRootName",
                    root."MediaTypeId",
                    file."RelativePath",
                    file."SizeBytes",
                    file."MissingSince",
                    file."MediaAssetId",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "WorkTitle"
                FROM
                    "StoredFiles" AS file
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    LEFT JOIN "Works" AS work ON work."Id" = asset."WorkId"
                WHERE
                    (@LibraryRootId::bigint IS NULL OR file."LibraryRootId" = @LibraryRootId)
                    AND (NOT @OnlyUnmatched OR file."MediaAssetId" IS NULL)
                    AND file."RelativePath" !~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'
                ORDER BY
                    file."LibraryRootId",
                    file."RelativePath"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new LibraryFileFilterData(Parameters.LibraryRootId, Parameters.OnlyUnmatched));
            sql.Page = Page;
            var rows = await Context.ReadSql.ReadPageAsync<LibraryFileRowData>(sql, CancellationToken);

            // Suggest identity from the path.
            var paths = rows.Items.ToDictionary(row => row.Id, row => MediaPathParser.Parse(row.RelativePath, row.MediaTypeId));
            var titles = paths.Values.Select(path => Limit(path.Title).ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    lower(title."Title") AS "Title",
                    work."MediaTypeId",
                    min(work."Id") AS "WorkId"
                FROM
                    "WorkTitles" AS title
                    JOIN "Works" AS work ON work."Id" = title."WorkId"
                WHERE
                    lower(title."Title") = ANY(@Titles)
                GROUP BY
                    lower(title."Title"),
                    work."MediaTypeId"
                HAVING
                    count(DISTINCT work."Id") = 1;
                """;
            sql.Parameters.Add(new WorkTitleLookupData(titles));
            var matches = titles.Length == 0 ? [] : await Context.ReadSql.ReadListAsync<WorkTitleMatchData>(sql, CancellationToken);

            var items = rows.Items.Select(row =>
            {
                var path = paths[row.Id];
                var title = Limit(path.Title);
                var match = matches.FirstOrDefault(item => item.MediaTypeId == row.MediaTypeId && item.Title == title.ToLowerInvariant());
                return new LibraryFileDetailsV1(row.Id, row.LibraryRootId, row.LibraryRootName, row.MediaTypeId, row.RelativePath, row.SizeBytes, row.MissingSince, row.MediaAssetId,
                    row.WorkId, row.WorkTitle, title, match?.WorkId, path.WorkUnitTypeId, path.UnitNumber, path.ParentUnitNumber);
            }).ToArray();
            return new PageResult<LibraryFileDetailsV1>(items, Page);
        }

        private static string Limit(string title) => title.Length > 500 ? title[..500] : title;
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/files/{storedFileId:long}/assignment")]
    public sealed class AssignLibraryFileV1 : AdminService<StoredFileParametersV1, AssignLibraryFileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AssignLibraryFileResultV1>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var data = new StoredFileAssignmentData(Parameters.StoredFileId, Data.WorkId, Data.Title, Data.WorkUnitTypeId, Data.UnitNumber, Data.ParentUnitNumber);
            return new AssignLibraryFileResultV1((await Context.AssignStoredFileAsync(data, CancellationToken)).Id);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/library/files/{storedFileId:long}/assignment")]
    public sealed class UnassignLibraryFileV1 : AdminService<StoredFileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.UnassignStoredFileAsync(new StoredFileParameters(Parameters.StoredFileId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/roots/{libraryRootId:long}/scans")]
    public sealed class QueueLibraryScanV1 : AdminService<LibraryRootParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<QueueLibraryScanResultV1>()];
        protected override ModuleRequirement GetInstanceModules(LibraryRootParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(LibraryRootParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            // The background worker performs storage I/O.
            var data = new OperationCreationData(OperationKind.LibraryScan, Parameters.LibraryRootId, Auth.AccountId);
            return new QueueLibraryScanResultV1(await Context.QueueLibraryScanAsync(data, CancellationToken));
        }
    }
}
