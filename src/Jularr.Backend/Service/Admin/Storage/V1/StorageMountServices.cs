using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/storage/mounts")]
    public sealed class ReadStorageMountsV1 : AdminService<ReadStorageMountsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<StorageMountDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadStorageMountsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadStorageMountsParametersV1 parameters, Type resultType) => new("storage.read");
        protected override PageRequest GetPageRequest(ReadStorageMountsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read configuration without NAS I/O.
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
                ORDER BY
                    mount."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<StorageMountDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts")]
    public sealed class CreateStorageMountV1 : AdminService<CreateStorageMountParametersV1, SaveStorageMountDataV1>
    {
        private readonly IDataProtector _protector;
        private readonly StorageOptions _options;

        internal CreateStorageMountV1(IDataProtectionProvider protection, IOptions<StorageOptions> options)
        {
            _protector = protection.CreateProtector(StoragePasswords.ProtectorPurpose);
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateStorageMountResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateStorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CreateStorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var data = StorageMountInput.Create(null, Data, _protector, _options);
            return new CreateStorageMountResultV1(await Context.CreateStorageMountAsync(data, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/storage/mounts/{storageMountId:long}")]
    public sealed class UpdateStorageMountV1 : AdminService<StorageMountParametersV1, SaveStorageMountDataV1>
    {
        private readonly IDataProtector _protector;
        private readonly StorageOptions _options;

        internal UpdateStorageMountV1(IDataProtectionProvider protection, IOptions<StorageOptions> options)
        {
            _protector = protection.CreateProtector(StoragePasswords.ProtectorPurpose);
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Saving never remounts implicitly.
            await Context.UpdateStorageMountAsync(StorageMountInput.Create(Parameters.StorageMountId, Data, _protector, _options), CancellationToken);
            return new UpdateResult(true);
        }
    }

    internal static class StorageMountInput
    {
        public static StorageMountSaveData Create(long? id, SaveStorageMountDataV1 data, IDataProtector protector, StorageOptions options)
        {
            // Local paths come from deployment configuration.
            if (data.StorageProtocolTypeId == StorageProtocolType.Local && !options.LocalPaths.Contains(data.SharePath, StringComparer.Ordinal))
            {
                throw new DataValidationException(nameof(SaveStorageMountDataV1.SharePath), "unknown_reference");
            }

            // Protect before persistence.
            var password = data.Password is null ? null : protector.Protect(Encoding.UTF8.GetBytes(data.Password));
            return new StorageMountSaveData(id, data.Name, data.StorageProtocolTypeId, data.Host, data.SharePath, data.ProtocolVersion, data.Username, data.Domain, data.IsReadOnly, password,
                data.WakeMacAddress, data.WakeBroadcastAddress, data.WakeOnDemand);
        }
    }
}
