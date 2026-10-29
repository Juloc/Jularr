using System.Security;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.AnnasArchive;

namespace Jularr.Backend.Service.Admin.Browser.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/browser")]
    public sealed class OpenDownloadBrowserAssistanceV1 : AdminService<ActivityOperationParametersV1, NoData>
    {
        private readonly BrowserAssistance _assistance;
        internal OpenDownloadBrowserAssistanceV1(BrowserAssistance assistance) => _assistance = assistance;
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BrowserViewV1>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Browser");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id" AS "AcquisitionConnectionId",
                    connection."BaseUrl",
                    operation."AttentionUrl"
                FROM
                    "Operations" AS operation
                    JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId"
                WHERE
                    operation."Id" = @OperationId
                    AND operation."OperationTypeId" = 2
                    AND operation."OperationStatusTypeId" IN (1, 2)
                    AND operation."AttentionKey" = 'source_manual_link'
                    AND operation."AttentionUrl" IS NOT NULL
                    AND operation."AttentionProposal" IS NULL
                    AND operation."SourceLinkUrl" IS NULL
                    AND connection."IsEnabled"
                    AND connection."AcquisitionConnectionTypeId" = 5;
                """;
            sql.Parameters.Add(Parameters);
            var download = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadSql.ReadOptionalAsync<BrowserAssistanceDownloadData>(sql, ct), CancellationToken, SqlAccessMode.ReadOnly)
                ?? throw new DataValidationException(nameof(Parameters.OperationId), "invalid_transition");
            if (!Uri.TryCreate(download.BaseUrl, UriKind.Absolute, out var source) || !Uri.TryCreate(download.AttentionUrl, UriKind.Absolute, out var page)
                || !AnnasArchiveClient.IsTrustedSource(source, page))
            {
                throw new DataValidationException(nameof(Parameters.OperationId), "invalid_transition");
            }

            return await _assistance.OpenAsync(Auth.SessionPublicId ?? throw new SecurityException(), download.AcquisitionConnectionId, page, CancellationToken, Parameters.OperationId);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/browser/assistance/{acquisitionConnectionId:long}")]
    public sealed class OpenBrowserAssistanceV1 : AdminService<AcquisitionConnectionParametersV1, NoData>
    {
        private readonly BrowserAssistance _assistance;
        internal OpenBrowserAssistanceV1(BrowserAssistance assistance) => _assistance = assistance;
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BrowserViewV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Browser");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var source = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(Parameters.AcquisitionConnectionId, ct), CancellationToken);
            if (!source.IsEnabled || source.AcquisitionConnectionTypeId != AcquisitionConnectionType.AnnasArchive)
            {
                throw new DataValidationException(nameof(Parameters.AcquisitionConnectionId), "invalid_transition");
            }

            return await _assistance.OpenAsync(Auth.SessionPublicId ?? throw new SecurityException(), source.Id, new Uri(source.HttpAddress), CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/browser/assistance/{leaseId:guid}/frame")]
    public sealed class ReadBrowserFrameV1 : AdminService<BrowserLeaseParametersV1>
    {
        private readonly BrowserAssistance _assistance;
        internal ReadBrowserFrameV1(BrowserAssistance assistance) => _assistance = assistance;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BrowserFrameV1>()];
        protected override ModuleRequirement GetInstanceModules(BrowserLeaseParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Browser");
        protected override ServicePermission GetPermission(BrowserLeaseParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override async Task<IServiceOutput> OnRead() => new BrowserFrameV1(await _assistance.FrameAsync(Auth.SessionPublicId ?? throw new SecurityException(), Parameters.LeaseId, CancellationToken));
    }

    [ServiceEndpoint("POST", "/api/v1/admin/browser/assistance/{leaseId:guid}/input")]
    public sealed class SendBrowserInputV1 : AdminService<BrowserLeaseParametersV1, BrowserInputDataV1>
    {
        private readonly BrowserAssistance _assistance;
        internal SendBrowserInputV1(BrowserAssistance assistance) => _assistance = assistance;
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BrowserLeaseParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Browser");
        protected override ServicePermission GetPermission(BrowserLeaseParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override async Task<IServiceOutput> OnExecute()
        {
            await _assistance.InputAsync(Auth.SessionPublicId ?? throw new SecurityException(), Parameters.LeaseId, Data, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/browser/assistance/{leaseId:guid}/close")]
    public sealed class CloseBrowserAssistanceV1 : AdminService<BrowserLeaseParametersV1, NoData>
    {
        private readonly BrowserAssistance _assistance;
        private readonly WorkSignals _signals;
        internal CloseBrowserAssistanceV1(BrowserAssistance assistance, WorkSignals signals)
        {
            _assistance = assistance;
            _signals = signals;
        }
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BrowserLeaseParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Browser");
        protected override ServicePermission GetPermission(BrowserLeaseParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override async Task<IServiceOutput> OnExecute()
        {
            var sourceId = await _assistance.CloseAsync(Auth.SessionPublicId ?? throw new SecurityException(), Parameters.LeaseId, CancellationToken);
            await Context.ExecuteBatchAsync((ctx, ct) => ctx.ResumeSourceDownloadsAsync(new AcquisitionConnectionParameters(sourceId), ct), CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _signals.Downloads.Notify();
    }
}
