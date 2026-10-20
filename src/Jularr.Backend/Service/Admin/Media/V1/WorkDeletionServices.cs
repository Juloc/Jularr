using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/deletion")]
    public sealed class DeleteWorkV1 : AdminService<WorkParametersV1, DeleteWorkDataV1>
    {
        private readonly LibraryReplacements _replacements;

        internal DeleteWorkV1(LibraryReplacements replacements)
        {
            _replacements = replacements;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // A running download would be left in its client; it is cancelled first.
            var facts = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWorkDeletionFactsAsync(Parameters, ct), CancellationToken);
            if (facts.HasActiveDownload)
            {
                throw new DataValidationException(nameof(DeleteWorkDataV1.DeletesFiles), "download_active");
            }

            // A file that cannot be removed, on a library that is not reachable, keeps the title: it never counts as deleted.
            if (Data.DeletesFiles && facts.StoredFileIds.Length > 0)
            {
                var (_, kept) = await _replacements.DeleteAsync(Context, facts.StoredFileIds, Auth.AccountId!.Value, CancellationToken);
                if (kept > 0)
                {
                    throw new DataValidationException(nameof(DeleteWorkDataV1.DeletesFiles), "files_stay");
                }
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RecordActivityAsync(new Data.Logic.Library.ActivityEventData(ActivityEventType.WorkDeleted, workId: Parameters.WorkId, accountId: Auth.AccountId), ct);
                await ctx.DeleteWorkAsync(new Data.Logic.Media.WorkDeletionData(Parameters.WorkId, Data.AddsExclusion), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    // Clears one language of a document's machine translation; the file and what readers noted stay.
    [ServiceEndpoint("DELETE", "/api/v1/admin/library/documents/{documentId:long}/translations/{language}")]
    public sealed class DeleteDocumentTranslationV1 : AdminService<DeleteTranslationParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteTranslationParametersV1 parameters) => ModuleRequirement.All("Library", "Reading", "Translation");
        protected override ServicePermission GetPermission(DeleteTranslationParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ReadingResources" AS resource
                WHERE
                    resource."ReadingDocumentId" = @DocumentId
                    AND resource."Path" LIKE ':translation/' || @Language || '/%';
                """;
            sql.Parameters.Add(Parameters);
            if (await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken) == 0)
            {
                throw new KeyNotFoundException("The translation was not found.");
            }

            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/works/exclusions")]
    public sealed class ReadWorkExclusionsV1 : AdminService<ReadWorkExclusionsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkExclusionV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadWorkExclusionsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadWorkExclusionsParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadWorkExclusionsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    exclusion."Id",
                    exclusion."MediaTypeId",
                    exclusion."Title",
                    exclusion."ReleaseYear"::integer AS "ReleaseYear",
                    exclusion."CreatedAt"
                FROM
                    "WorkExclusions" AS exclusion
                ORDER BY
                    exclusion."Title",
                    exclusion."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkExclusionV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/works/exclusions/{workExclusionId:long}")]
    public sealed class DeleteWorkExclusionV1 : AdminService<WorkExclusionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkExclusionParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkExclusionParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteWorkExclusionAsync(Parameters, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
