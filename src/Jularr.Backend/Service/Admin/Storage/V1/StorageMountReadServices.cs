using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/storage/mounts/{storageMountId:long}")]
    public sealed class ReadStorageMountV1 : AdminService<StorageMountParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<StorageMountDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Id",
                    mount."Name",
                    mount."StorageProtocolTypeId",
                    mount."Host",
                    mount."SharePath",
                    mount."ProtocolVersion",
                    mount."Username",
                    mount."Domain",
                    mount."IsReadOnly",
                    mount."ProtectedPassword" IS NOT NULL AS "HasPassword",
                    mount."WakeMacAddress",
                    mount."WakeBroadcastAddress",
                    mount."WakeOnDemand",
                    mount."StorageProtocolTypeId" <> 3 AS "IsNetwork",
                    mount."IsVolume",
                    mount."Version",
                    health."StorageHealthTypeId",
                    health."DiagnosticCode",
                    health."CheckedAt",
                    health."FreeBytes",
                    health."TotalBytes",
                    mount."SynologyUrl",
                    mount."SynologyUsername"
                FROM
                    "StorageMounts" AS mount
                    JOIN "StorageMountHealth" AS health ON health."StorageMountId" = mount."Id"
                WHERE
                    mount."Id" = @StorageMountId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<StorageMountDetailsV1>(sql, CancellationToken);
        }
    }

    // The compose lines that mount a NAS share as a Docker volume; the password stays a variable for the .env file.
    [ServiceEndpoint("GET", "/api/v1/admin/storage/mounts/{storageMountId:long}/volume")]
    public sealed class ReadStorageMountVolumeV1 : AdminService<StorageMountParametersV1>
    {
        private readonly StorageOptions _options;

        internal ReadStorageMountVolumeV1(IOptions<StorageOptions> options)
        {
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<StorageMountVolumeV1>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var mount = await Context.ReadStorageMountAsync(Parameters.StorageMountId, CancellationToken);
            if (!mount.IsVolume)
            {
                throw new DataValidationException(nameof(StorageMountDetailsV1.StorageProtocolTypeId), "invalid_type");
            }

            return new StorageMountVolumeV1(StorageVolumeLogic.ComposeBlock(mount, _options.MountRoot));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/storage/protocols")]
    public sealed class ReadStorageProtocolsV1 : AdminService<ReadStorageProtocolsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<StorageProtocolVersionDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadStorageProtocolsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadStorageProtocolsParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Expose the supported catalog.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    protocol."StorageProtocolTypeId",
                    protocol."ProtocolVersion"
                FROM
                    "StorageProtocolVersions" AS protocol
                ORDER BY
                    protocol."StorageProtocolTypeId",
                    protocol."ProtocolVersion" DESC;
                """;
            return new BaseDataList<StorageProtocolVersionDetailsV1>(await Context.ReadSql.ReadListAsync<StorageProtocolVersionDetailsV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/storage/local-paths")]
    public sealed class ReadStorageLocalPathsV1 : AdminService<ReadStorageLocalPathsParametersV1>
    {
        private readonly StorageOptions _options;

        internal ReadStorageLocalPathsV1(IOptions<StorageOptions> options)
        {
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<StorageLocalPathV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadStorageLocalPathsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadStorageLocalPathsParametersV1 parameters, Type resultType) => new("storage.read");

        protected override Task<IServiceOutput> OnRead() =>
            Task.FromResult<IServiceOutput>(new BaseDataList<StorageLocalPathV1>(_options.LocalPaths.Order(StringComparer.Ordinal).Select(path => new StorageLocalPathV1(path)).ToArray()));
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/directories")]
    public sealed class ReadStorageDirectoriesV1 : AdminService<StorageMountParametersV1, ReadStorageDirectoriesDataV1>
    {
        private readonly StorageAccess _storage;

        internal ReadStorageDirectoriesV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<StorageDirectoryV1>>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Browse only below the connection.
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(Parameters.StorageMountId, ct), CancellationToken);
            var names = await _storage.Open(mount).ListDirectoriesAsync(Data.RelativePath, StorageDirectoryV1.PageSize, CancellationToken, Data.AfterName);
            return new BaseDataList<StorageDirectoryV1>(names.Select(name => new StorageDirectoryV1(name)).ToArray());
        }
    }
}
