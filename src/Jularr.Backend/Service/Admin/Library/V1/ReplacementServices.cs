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
    [ServiceEndpoint("GET", "/api/v1/admin/library/replacement")]
    public sealed class ReadReplacementSettingsV1 : AdminService<ReplacementSettingsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReplacementSettingsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReplacementSettingsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReplacementSettingsParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."KeepsReleases",
                    settings."RecycleModeId",
                    settings."RecycleStorageMountId",
                    settings."RecyclePath",
                    settings."RecycleDays",
                    settings."UnmonitorsDeletedUnits",
                    settings."Version"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return await Context.ReadSql.ReadRequiredAsync<ReplacementSettingsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/replacement")]
    public sealed class UpdateReplacementSettingsV1 : AdminService<ReplacementSettingsParametersV1, UpdateReplacementSettingsDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReplacementSettingsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReplacementSettingsParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveReplacementSettingsAsync(new ReplacementSettingsSaveData(Data.KeepsReleases, Data.RecycleModeId, Data.RecycleStorageMountId, Data.RecyclePath,
                Data.RecycleDays, Data.Version, Data.UnmonitorsDeletedUnits), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/files/{storedFileId:long}/keep-only")]
    public sealed class KeepOnlyLibraryFileV1 : AdminService<StoredFileParametersV1, NoData>
    {
        private readonly LibraryReplacements _replacements;

        internal KeepOnlyLibraryFileV1(LibraryReplacements replacements)
        {
            _replacements = replacements;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The administrator's confirmation: the other versions of this file's film, episode or volume go, also better ones.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."MediaAssetId" AS "Id"
                FROM
                    "StoredFiles" AS file
                WHERE
                    file."Id" = @StoredFileId
                    AND file."MediaAssetId" IS NOT NULL
                    AND file."MissingSince" IS NULL;
                """;
            sql.Parameters.Add(Parameters);
            var asset = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadSql.ReadOptionalAsync<MediaAssetIdData>(sql, ct), CancellationToken)
                ?? throw new DataValidationException(nameof(StoredFileParametersV1.StoredFileId), "invalid_transition");
            await _replacements.KeepOnlyAsync(Context, asset.Id, Auth.AccountId!.Value, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/files/deletion")]
    public sealed class DeleteLibraryFilesV1 : AdminService<DeleteLibraryFilesParametersV1, DeleteLibraryFilesDataV1>
    {
        private readonly LibraryReplacements _replacements;

        internal DeleteLibraryFilesV1(LibraryReplacements replacements)
        {
            _replacements = replacements;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DeleteLibraryFilesResultV1>()];
        protected override ModuleRequirement GetInstanceModules(DeleteLibraryFilesParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DeleteLibraryFilesParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // At once and without a second question, like Sonarr: the setting of replaced files decides between deleting and recycling.
            var (deleted, kept) = await _replacements.DeleteAsync(Context, Data.StoredFileIds, Auth.AccountId!.Value, CancellationToken);
            return new DeleteLibraryFilesResultV1(deleted, kept);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/files/{storedFileId:long}/protection")]
    public sealed class ProtectLibraryFileV1 : AdminService<StoredFileParametersV1, ProtectLibraryFileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.ProtectStoredFileAsync(new StoredFileProtectionData(Parameters.StoredFileId, Data.IsProtected), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
