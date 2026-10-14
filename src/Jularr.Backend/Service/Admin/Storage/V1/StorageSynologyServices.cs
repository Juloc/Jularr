using System.Security.Cryptography;
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
using Jularr.Integrations.Data.Synology;
using Jularr.Integrations.Logic.Synology;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    [ServiceEndpoint("PUT", "/api/v1/admin/storage/mounts/{storageMountId:long}/synology")]
    public sealed class UpdateStorageSynologyV1 : AdminService<StorageMountParametersV1, UpdateStorageSynologyDataV1>
    {
        private readonly IDataProtector _protector;

        internal UpdateStorageSynologyV1(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector(StorageMountRequests.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var password = Data.SynologyUrl is not null && Data.Password is { } clear ? _protector.Protect(Encoding.UTF8.GetBytes(clear)) : null;
            try
            {
                await Context.UpdateStorageSynologyAsync(new StorageSynologyData(Parameters.StorageMountId, Data.SynologyUrl, Data.SynologyUrl is null ? null : Data.SynologyUsername, password),
                    CancellationToken);
            }
            catch (DataValidationException exception) when (exception.Field == nameof(StorageSynologyData.SynologyProtectedPassword))
            {
                throw new DataValidationException(nameof(UpdateStorageSynologyDataV1.Password), "required");
            }

            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/synology/status")]
    public sealed class ReadStorageSynologyStatusV1 : AdminService<StorageMountParametersV1, NoData>
    {
        private readonly SynologyClient _synology;
        private readonly IDataProtector _protector;

        internal ReadStorageSynologyStatusV1(SynologyClient synology, IDataProtectionProvider protection)
        {
            _synology = synology;
            _protector = protection.CreateProtector(StorageMountRequests.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SynologyStatusV1>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var access = await Context.ExecuteBatchAsync((ctx, ct) => StorageSynology.ReadAccessAsync(ctx, Parameters.StorageMountId, ct), CancellationToken);
            var status = await StorageSynology.RunAsync(access, _protector, credentials => _synology.ReadStatusAsync(credentials, CancellationToken));
            return new SynologyStatusV1(status.Model, status.Version, status.Temperature,
                new BaseDataList<SynologyVolumeV1>(status.Volumes.Select(volume => new SynologyVolumeV1(volume.Name, volume.Status, volume.TotalBytes, volume.UsedBytes))),
                new BaseDataList<SynologyDiskV1>(status.Disks.Select(disk => new SynologyDiskV1(disk.Name, disk.Model, disk.Status, disk.Temperature))));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/synology/power")]
    public sealed class PowerStorageSynologyV1 : AdminService<StorageMountParametersV1, PowerStorageSynologyDataV1>
    {
        private readonly SynologyClient _synology;
        private readonly IDataProtector _protector;

        internal PowerStorageSynologyV1(SynologyClient synology, IDataProtectionProvider protection)
        {
            _synology = synology;
            _protector = protection.CreateProtector(StorageMountRequests.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var access = await Context.ExecuteBatchAsync((ctx, ct) => StorageSynology.ReadAccessAsync(ctx, Parameters.StorageMountId, ct), CancellationToken);
            await StorageSynology.RunAsync(access, _protector, async credentials =>
            {
                await _synology.PowerAsync(credentials, Data.SynologyPowerTypeId == SynologyPowerType.Restart, CancellationToken);
                return true;
            });
            return new UpdateResult(true);
        }
    }

    // Decrypts the DSM password for one call and maps DSM failures to service errors.
    internal static class StorageSynology
    {
        public static async Task<SynologyCertificate?> ReadCertificateAsync(SynologyClient synology, StorageSynologyData access, CancellationToken ct)
        {
            if (access.SynologyUrl is not { } url)
            {
                throw new DataValidationException(nameof(UpdateStorageSynologyDataV1.SynologyUrl), "required");
            }

            try
            {
                return await synology.ReadCertificateAsync(Address(url), ct);
            }
            catch (SynologyException exception)
            {
                throw new ServiceUnavailableException(exception.Code);
            }
        }

        public static Task<StorageSynologyData> ReadAccessAsync(LogicContext ctx, long storageMountId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Id" AS "StorageMountId",
                    mount."SynologyUrl",
                    mount."SynologyUsername",
                    mount."SynologyProtectedPassword",
                    mount."SynologyCertificateSha256"
                FROM
                    "StorageMounts" AS mount
                WHERE
                    mount."Id" = @StorageMountId;
                """;
            sql.Parameters.Add(new StorageMountParameters(storageMountId));
            return ctx.ReadSql.ReadRequiredAsync<StorageSynologyData>(sql, ct);
        }

        public static Uri Address(string url) => new(url.EndsWith('/') ? url : url + "/");

        public static async Task<T> RunAsync<T>(StorageSynologyData access, IDataProtector protector, Func<SynologyAccess, Task<T>> call)
        {
            if (access is not { SynologyUrl: { } url, SynologyUsername: { } username, SynologyProtectedPassword: { } protectedPassword })
            {
                throw new DataValidationException(nameof(UpdateStorageSynologyDataV1.SynologyUrl), "required");
            }

            string password;
            try
            {
                var clear = protector.Unprotect(protectedPassword);
                password = Encoding.UTF8.GetString(clear);
                CryptographicOperations.ZeroMemory(clear);
            }
            catch (CryptographicException)
            {
                throw new ServiceUnavailableException("synology_login_failed");
            }

            // DSM I/O outside transactions.
            try
            {
                return await call(new SynologyAccess(Address(url), username, password, access.SynologyCertificateSha256));
            }
            catch (SynologyException exception)
            {
                throw new ServiceUnavailableException(exception.Code);
            }
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/synology/certificate")]
    public sealed class ReadStorageSynologyCertificateV1 : AdminService<StorageMountParametersV1, NoData>
    {
        private readonly SynologyClient _synology;

        internal ReadStorageSynologyCertificateV1(SynologyClient synology)
        {
            _synology = synology;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SynologyCertificateV1>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        // What the NAS presents now, beside what the Owner confirmed.
        protected override async Task<IServiceOutput> OnExecute()
        {
            var access = await Context.ExecuteBatchAsync((ctx, ct) => StorageSynology.ReadAccessAsync(ctx, Parameters.StorageMountId, ct), CancellationToken);
            var certificate = await StorageSynology.ReadCertificateAsync(_synology, access, CancellationToken);
            return new SynologyCertificateV1(certificate?.Sha256, certificate?.IsValid ?? false,
                certificate is not null && string.Equals(certificate.Sha256, access.SynologyCertificateSha256, StringComparison.OrdinalIgnoreCase));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/synology/certificate/trust")]
    public sealed class TrustStorageSynologyCertificateV1 : AdminService<StorageMountParametersV1, TrustSynologyCertificateDataV1>
    {
        private readonly SynologyClient _synology;

        internal TrustStorageSynologyCertificateV1(SynologyClient synology)
        {
            _synology = synology;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        // Only the certificate the NAS still presents, and the Owner saw, is trusted.
        protected override async Task<IServiceOutput> OnExecute()
        {
            var access = await Context.ExecuteBatchAsync((ctx, ct) => StorageSynology.ReadAccessAsync(ctx, Parameters.StorageMountId, ct), CancellationToken);
            var certificate = await StorageSynology.ReadCertificateAsync(_synology, access, CancellationToken);
            if (certificate is null || access.SynologyUrl is not { } url || !string.Equals(certificate.Sha256, Data.SynologyCertificateSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DataValidationException(nameof(Data.SynologyCertificateSha256), "invalid_transition");
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.TrustStorageSynologyCertificateAsync(new StorageSynologyCertificateData(Parameters.StorageMountId, url, certificate.Sha256), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
