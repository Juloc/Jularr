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
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Data.Subtitles;
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
            // Indexers first, then download clients; a role keeps one kind.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    connection."Host",
                    connection."Port",
                    connection."UseSsl",
                    connection."ProtectedApiKey" IS NOT NULL AS "HasApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemovesCompleted",
                    connection."RemovesFailed",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."IncompleteRemotePath",
                    connection."IncompleteLocalPath",
                    connection."DownloadStorageMountId",
                    connection."Username",
                    connection."MaxConnections",
                    connection."CheckedAt",
                    connection."LastErrorKey" AS "ErrorKey",
                    connection."Version",
                    connection."MediaTypeIds",
                    connection."SearchesAutomatically",
                    connection."QueryLimit",
                    connection."GrabLimit",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."QueryCount" ELSE 0 END AS "QueryCount",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."GrabCount" ELSE 0 END AS "GrabCount",
                    connection."RetryAt",
                    COALESCE(connection."RetryAt" > now(), FALSE) AS "IsBackedOff",
                    type."AcquisitionConnectionRoleId"
                FROM
                    "AcquisitionConnections" AS connection
                    JOIN "AcquisitionConnectionTypes" AS type ON type."Id" = connection."AcquisitionConnectionTypeId"
                WHERE
                    @AcquisitionConnectionRoleId::smallint IS NULL
                    OR type."AcquisitionConnectionRoleId" = @AcquisitionConnectionRoleId
                ORDER BY
                    connection."AcquisitionConnectionTypeId",
                    connection."Priority",
                    connection."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new AcquisitionConnectionRoleFilterData(Parameters.AcquisitionConnectionRoleId));
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
                    connection."Host",
                    connection."Port",
                    connection."UseSsl",
                    connection."ProtectedApiKey" IS NOT NULL AS "HasApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemovesCompleted",
                    connection."RemovesFailed",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."IncompleteRemotePath",
                    connection."IncompleteLocalPath",
                    connection."DownloadStorageMountId",
                    connection."Username",
                    connection."MaxConnections",
                    connection."CheckedAt",
                    connection."LastErrorKey" AS "ErrorKey",
                    connection."Version",
                    connection."MediaTypeIds",
                    connection."SearchesAutomatically",
                    connection."QueryLimit",
                    connection."GrabLimit",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."QueryCount" ELSE 0 END AS "QueryCount",
                    CASE WHEN connection."CountedOn" = current_date THEN connection."GrabCount" ELSE 0 END AS "GrabCount",
                    connection."RetryAt",
                    COALESCE(connection."RetryAt" > now(), FALSE) AS "IsBackedOff",
                    type."AcquisitionConnectionRoleId"
                FROM
                    "AcquisitionConnections" AS connection
                    JOIN "AcquisitionConnectionTypes" AS type ON type."Id" = connection."AcquisitionConnectionTypeId"
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
        private readonly SabnzbdClient _sabnzbd;

        internal CreateAcquisitionConnectionV1(IDataProtectionProvider protection, SabnzbdClient sabnzbd)
        {
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _sabnzbd = sabnzbd;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateAcquisitionConnectionResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateAcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(CreateAcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnCreate()
        {
            // OpenSubtitles downloads need its account.
            if (Data.AcquisitionConnectionTypeId == AcquisitionConnectionType.OpenSubtitles && (Data.Username is null || Data.Password is null))
            {
                throw new DataValidationException(Data.Username is null ? nameof(SaveAcquisitionConnectionDataV1.Username) : nameof(SaveAcquisitionConnectionDataV1.Password), "required");
            }

            var detected = await AcquisitionConnectionInput.DiscoverCompleteFolderAsync(Data, null, _protector, _sabnzbd, CancellationToken);
            var data = AcquisitionConnectionInput.Create(null, Data, _protector, detected);
            return new CreateAcquisitionConnectionResultV1(await Context.CreateAcquisitionConnectionAsync(data, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}")]
    public sealed class UpdateAcquisitionConnectionV1 : AdminService<AcquisitionConnectionParametersV1, SaveAcquisitionConnectionDataV1>
    {
        private readonly IDataProtector _protector;
        private readonly SabnzbdClient _sabnzbd;

        internal UpdateAcquisitionConnectionV1(IDataProtectionProvider protection, SabnzbdClient sabnzbd)
        {
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _sabnzbd = sabnzbd;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // This read is authorized by the service, but must not open a short write batch.
            // Only a SAB connection with a stored secret needs its previous protected key.
            AcquisitionConnectionData? stored = null;
            if (Data.AcquisitionConnectionTypeId == AcquisitionConnectionType.Sabnzbd && Data.ApiKey is null && Data.RemotePath is null
                && (Data.DownloadStorageMountId is not null || Data.LocalPath is not null))
            {
                stored = await Context.ReadSql.ReadRequiredAsync<AcquisitionConnectionData>(
                    AcquisitionConnectionLogic.ReadAcquisitionConnectionStatement(Parameters.AcquisitionConnectionId), CancellationToken);
            }

            var detected = await AcquisitionConnectionInput.DiscoverCompleteFolderAsync(Data, stored, _protector, _sabnzbd, CancellationToken);
            await Context.UpdateAcquisitionConnectionAsync(AcquisitionConnectionInput.Create(Parameters.AcquisitionConnectionId, Data, _protector, detected), CancellationToken);
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
        private readonly AcquisitionConnectionProbe _probe;

        internal TestAcquisitionConnectionV1(AcquisitionConnectionProbe probe)
        {
            _probe = probe;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AcquisitionConnectionTestV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(Parameters.AcquisitionConnectionId, ct), CancellationToken);

            // Keep remote I/O outside transactions.
            var (result, capabilities) = await _probe.TestAsync(connection, CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveAcquisitionConnectionCheckAsync(new AcquisitionConnectionCheckData(connection.Id, connection.Version, capabilities, result.ErrorKey), ct);
                return true;
            }, CancellationToken);
            return result;
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/search")]
    public sealed class SearchAcquisitionConnectionV1 : AdminService<AcquisitionConnectionParametersV1, SearchAcquisitionConnectionDataV1>
    {
        private const int Limit = 25;
        private readonly NewznabClient _newznab;
        private readonly IDataProtector _protector;

        internal SearchAcquisitionConnectionV1(NewznabClient newznab, IDataProtectionProvider protection)
        {
            _newznab = newznab;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<IndexerReleaseV1>>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // One free-text query of an indexer, counted like any other.
            var indexer = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(Parameters.AcquisitionConnectionId, ct), CancellationToken);
            if (indexer.AcquisitionConnectionTypeId != AcquisitionConnectionType.Newznab)
            {
                throw new DataValidationException(nameof(SearchAcquisitionConnectionDataV1.Query), "invalid_transition");
            }

            if (!AcquisitionConnectionLogic.HasQueries(indexer, 0))
            {
                throw new ServiceUnavailableException("acquisition_query_limit_reached");
            }

            IReadOnlyList<NewznabItem> items = [];
            string? failure = null;
            try
            {
                items = await _newznab.SearchAsync(new NewznabConnection(indexer.HttpAddress, AcquisitionConnectionInput.IndexerKey(_protector, indexer.ProtectedApiKey)),
                    new NewznabQuery(NewznabSearchMode.Search, Data.Query, [], indexer.Categories, Limit), CancellationToken);
            }
            catch (NewznabException exception)
            {
                failure = AcquisitionConnectionInput.ErrorKey(exception.Failure);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RecordIndexerUseAsync(new IndexerUseData(indexer.Id, 1, failure is null), ct);
                return true;
            }, CancellationToken);
            return failure is not null
                ? throw new ServiceUnavailableException(failure)
                : new BaseDataList<IndexerReleaseV1>([.. items.Select(item => new IndexerReleaseV1(TextLimit.Cut(item.Title, 1000)!, item.SizeBytes, item.PublishedAt?.UtcDateTime))]);
        }
    }

    internal static class AcquisitionConnectionInput
    {
        public const string ProtectorPurpose = "Jularr.Acquisition.ApiKey.V1";

        public static async Task<string?> DiscoverCompleteFolderAsync(SaveAcquisitionConnectionDataV1 data, AcquisitionConnectionData? stored,
            IDataProtector protector, SabnzbdClient sabnzbd, CancellationToken ct)
        {
            // Existing explicit API mappings remain valid; the regular UI no longer asks for a remote path.
            if (data.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd || data.RemotePath is not null
                || data.DownloadStorageMountId is null && data.LocalPath is null) return null;

            var key = data.ApiKey ?? (stored is { AcquisitionConnectionTypeId: AcquisitionConnectionType.Sabnzbd }
                ? Unprotect(protector, stored.ProtectedApiKey) : null);
            if (data.BaseUrl is null || key is null) throw new ServiceUnavailableException("acquisition_unavailable");
            try
            {
                return await sabnzbd.GetCompletedFolderAsync(new SabnzbdConnection(data.BaseUrl, key), data.DownloadCategory, ct);
            }
            catch (Exception exception) when (exception is SabnzbdException or JsonException)
            {
                throw new ServiceUnavailableException("acquisition_invalid_response");
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("acquisition_unavailable");
            }
        }

        public static AcquisitionConnectionSaveData Create(long? id, SaveAcquisitionConnectionDataV1 data, IDataProtector protector, string? detectedRemotePath = null)
        {
            // Protect before persistence.
            var apiKey = data.ApiKey is null ? null : protector.Protect(Encoding.UTF8.GetBytes(data.ApiKey));
            var password = data.AcquisitionConnectionTypeId == AcquisitionConnectionType.OpenSubtitles && data.Password is { } clear ? protector.Protect(Encoding.UTF8.GetBytes(clear)) : null;
            return new AcquisitionConnectionSaveData(id, data.Name, data.AcquisitionConnectionTypeId, data.BaseUrl, apiKey, data.IsEnabled, data.Priority,
                data.Categories, data.DownloadCategory, detectedRemotePath ?? data.RemotePath, data.LocalPath,
                data.AcquisitionConnectionTypeId is AcquisitionConnectionType.Opds or AcquisitionConnectionType.Usenet or AcquisitionConnectionType.OpenSubtitles ? data.Username : null,
                data.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet ? data.MaxConnections ?? 8 : null, password, data.MediaTypeIds,
                data.SearchesAutomatically, data.QueryLimit, data.GrabLimit, data.Host, data.Port, data.UseSsl, data.DownloadStorageMountId,
                data.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd || data.RemovesCompleted, data.IncompleteRemotePath, data.IncompleteLocalPath, data.RemovesFailed);
        }

        // Keyless indexers send no key.
        public static string IndexerKey(IDataProtector protector, byte[]? protectedApiKey) => protectedApiKey is null ? "" : Unprotect(protector, protectedApiKey);

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
