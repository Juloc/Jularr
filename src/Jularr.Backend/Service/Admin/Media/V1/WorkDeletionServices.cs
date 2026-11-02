using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Qbittorrent;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // What discards a running download of a title that is deleted with its data: the client's job and files go, the operation closes as removed.
    internal sealed class WorkDownloadClients(SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadStorage downloads, TimeProvider time, IDataProtector protector)
    {
        public async Task DiscardAsync(LogicContext context, long operationId, CancellationToken ct)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var download = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDownloadActionAsync(operationId, token), ct);
            if (download is not null)
            {
                await DownloadActions.RemoveAsync(context, sabnzbd, qbittorrent, downloads, protector, now, download, false, ct);
                return;
            }

            // A download no client holds yet or any more only needs to close.
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.RemoveDownloadAsync(new DownloadRemovalData(operationId, false, now), token);
                return true;
            }, ct);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/deletion")]
    public sealed class DeleteWorkV1 : AdminService<WorkParametersV1, DeleteWorkDataV1>
    {
        private readonly LibraryReplacements _replacements;
        private readonly CoverStore _covers;
        private readonly WorkDownloadClients _clients;

        internal DeleteWorkV1(LibraryReplacements replacements, CoverStore covers, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DownloadStorage downloads, TimeProvider time, IDataProtectionProvider protection)
        {
            _replacements = replacements;
            _covers = covers;
            _clients = new WorkDownloadClients(sabnzbd, qbittorrent, downloads, time, protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose));
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await DeleteAsync(Context, _replacements, _covers, _clients, Parameters.WorkId, Data.DeletesFiles, Data.AddsExclusion, Auth.AccountId!.Value, CancellationToken);
            return new UpdateResult(true);
        }

        // One title as Sonarr's dialog deletes it; the mass editor does the same for each chosen title.
        internal static async Task DeleteAsync(LogicContext context, LibraryReplacements replacements, CoverStore covers, WorkDownloadClients clients, long workId, bool deletesFiles, bool addsExclusion, long accountId, CancellationToken ct)
        {
            // A running download would be left in its client: deleting with its data discards it first, deleting only the title refuses.
            var facts = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadWorkDeletionFactsAsync(new WorkParametersV1(workId), token), ct);
            if (facts.ActiveDownloadIds.Length > 0)
            {
                if (!deletesFiles)
                {
                    throw new DataValidationException(nameof(DeleteWorkDataV1.DeletesFiles), "download_active");
                }

                foreach (var operationId in facts.ActiveDownloadIds)
                {
                    await clients.DiscardAsync(context, operationId, ct);
                }
            }

            // A file that cannot be removed, on a library that is not reachable, keeps the title: it never counts as deleted.
            if (deletesFiles && facts.StoredFileIds.Length > 0)
            {
                var (_, kept) = await replacements.DeleteAsync(context, facts.StoredFileIds, accountId, ct);
                if (kept > 0)
                {
                    throw new DataValidationException(nameof(DeleteWorkDataV1.DeletesFiles), "files_stay");
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.RecordActivityAsync(new Data.Logic.Library.ActivityEventData(ActivityEventType.WorkDeleted, workId: workId, accountId: accountId), token);
                await ctx.DeleteWorkAsync(new Data.Logic.Media.WorkDeletionData(workId, addsExclusion), token);
                return true;
            }, ct);

            // The title's covers leave with it.
            covers.Delete(workId);
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

    [ServiceEndpoint("PUT", "/api/v1/admin/works/{workId:long}/editions/{workEditionId:long}")]
    public sealed class UpdateWorkEditionV1 : AdminService<WorkEditionParametersV1, UpdateWorkEditionDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkEditionParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkEditionParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkEditions" AS edition
                SET
                    "IsMonitored" = @IsMonitored
                WHERE
                    edition."Id" = @WorkEditionId
                    AND edition."WorkId" = @WorkId;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(Data);
            if (await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken) == 0)
            {
                throw new KeyNotFoundException("The edition was not found.");
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
