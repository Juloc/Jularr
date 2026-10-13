using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/connections")]
    public sealed class ReadAcquisitionConnectionsV1 : AdminService<ReadAcquisitionConnectionsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AcquisitionConnectionDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadAcquisitionConnectionsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReadAcquisitionConnectionsParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override PageRequest GetPageRequest(ReadAcquisitionConnectionsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Indexers first, then download clients.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    TRUE AS "HasApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."Username",
                    connection."CheckedAt",
                    connection."LastErrorKey" AS "ErrorKey",
                    connection."Version"
                FROM
                    "AcquisitionConnections" AS connection
                ORDER BY
                    connection."AcquisitionConnectionTypeId",
                    connection."Priority",
                    connection."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<AcquisitionConnectionDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}")]
    public sealed class ReadAcquisitionConnectionV1 : AdminService<AcquisitionConnectionParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AcquisitionConnectionDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    TRUE AS "HasApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."Username",
                    connection."CheckedAt",
                    connection."LastErrorKey" AS "ErrorKey",
                    connection."Version"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<AcquisitionConnectionDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections")]
    public sealed class CreateAcquisitionConnectionV1 : AdminService<CreateAcquisitionConnectionParametersV1, SaveAcquisitionConnectionDataV1>
    {
        private readonly IDataProtector _protector;

        internal CreateAcquisitionConnectionV1(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateAcquisitionConnectionResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateAcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(CreateAcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnCreate()
        {
            // New connections carry a key unless the source needs none.
            if (Data.ApiKey is null && Data.AcquisitionConnectionTypeId is not (AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Opds))
            {
                throw new DataValidationException(nameof(SaveAcquisitionConnectionDataV1.ApiKey), "required");
            }

            var data = AcquisitionConnectionInput.Create(null, Data, _protector);
            return new CreateAcquisitionConnectionResultV1(await Context.CreateAcquisitionConnectionAsync(data, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}")]
    public sealed class UpdateAcquisitionConnectionV1 : AdminService<AcquisitionConnectionParametersV1, SaveAcquisitionConnectionDataV1>
    {
        private readonly IDataProtector _protector;

        internal UpdateAcquisitionConnectionV1(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateAcquisitionConnectionAsync(AcquisitionConnectionInput.Create(Parameters.AcquisitionConnectionId, Data, _protector), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}")]
    public sealed class DeleteAcquisitionConnectionV1 : AdminService<DeleteAcquisitionConnectionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteAcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DeleteAcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnDelete()
        {
            // Release history keeps its rows without the connection.
            await Context.DeleteAcquisitionConnectionAsync(new AcquisitionConnectionParameters(Parameters.AcquisitionConnectionId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/test")]
    public sealed class TestAcquisitionConnectionV1 : AdminService<AcquisitionConnectionParametersV1, NoData>
    {
        private readonly NewznabClient _newznab;
        private readonly SabnzbdClient _sabnzbd;
        private readonly DirectSources _direct;
        private readonly IDataProtector _protector;

        internal TestAcquisitionConnectionV1(NewznabClient newznab, SabnzbdClient sabnzbd, DirectSources direct, IDataProtectionProvider protection)
        {
            _newznab = newznab;
            _sabnzbd = sabnzbd;
            _direct = direct;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AcquisitionConnectionTestV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(Parameters.AcquisitionConnectionId, ct), CancellationToken);
            var apiKey = DirectSources.IsDirect(connection.AcquisitionConnectionTypeId) ? "" : AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey);

            // Keep remote I/O outside transactions.
            AcquisitionConnectionTestV1 result;
            string? capabilities = null;
            if (DirectSources.IsDirect(connection.AcquisitionConnectionTypeId))
            {
                var error = await _direct.TestAsync(connection, CancellationToken);
                result = new AcquisitionConnectionTestV1(error is null, error, null, error is null ? ["Book"] : []);
            }
            else if (connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab)
            {
                try
                {
                    var caps = await _newznab.ReadCapabilitiesAsync(new NewznabConnection(connection.BaseUrl, apiKey), CancellationToken);
                    capabilities = JsonSerializer.Serialize(caps);
                    result = new AcquisitionConnectionTestV1(true, null, caps.Version, caps.Modes.Keys.Order().Select(mode => mode.ToString()).ToArray());
                }
                catch (NewznabException exception)
                {
                    result = new AcquisitionConnectionTestV1(false, AcquisitionConnectionInput.ErrorKey(exception.Failure), null, []);
                }
            }
            else
            {
                var test = await _sabnzbd.TestAsync(new SabnzbdConnection(connection.BaseUrl, apiKey), CancellationToken);
                var errorKey = test.IsAvailable ? null : test.Version is null ? "acquisition_unavailable" : "acquisition_authentication_failed";
                result = new AcquisitionConnectionTestV1(test.IsAvailable, errorKey, test.Version, []);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveAcquisitionConnectionCheckAsync(new AcquisitionConnectionCheckData(connection.Id, connection.Version, capabilities, result.ErrorKey), ct);
                return true;
            }, CancellationToken);
            return result;
        }
    }

    internal static class AcquisitionConnectionInput
    {
        public const string ProtectorPurpose = "Jularr.Acquisition.ApiKey.V1";

        public static AcquisitionConnectionSaveData Create(long? id, SaveAcquisitionConnectionDataV1 data, IDataProtector protector)
        {
            // Protect before persistence.
            var apiKey = data.ApiKey is null ? null : protector.Protect(Encoding.UTF8.GetBytes(data.ApiKey));
            return new AcquisitionConnectionSaveData(id, data.Name, data.AcquisitionConnectionTypeId, data.BaseUrl.TrimEnd('/'), apiKey, data.IsEnabled, data.Priority,
                data.Categories.Distinct().Order().ToArray(), data.DownloadCategory, data.RemotePath, data.LocalPath,
                data.AcquisitionConnectionTypeId == AcquisitionConnectionType.Opds ? data.Username : null);
        }

        public static string Unprotect(IDataProtector protector, byte[]? protectedApiKey)
        {
            if (protectedApiKey is null)
            {
                throw new ServiceUnavailableException("acquisition_unavailable");
            }

            byte[] clear;
            try
            {
                clear = protector.Unprotect(protectedApiKey);
            }
            catch (CryptographicException)
            {
                throw new ServiceUnavailableException("acquisition_unavailable");
            }

            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        public static string ErrorKey(NewznabFailure failure) => failure switch
        {
            NewznabFailure.AuthenticationFailed => "acquisition_authentication_failed",
            NewznabFailure.RateLimited => "acquisition_rate_limited",
            NewznabFailure.ParametersRejected or NewznabFailure.InvalidResponse => "acquisition_invalid_response",
            _ => "acquisition_unavailable"
        };
    }
}
