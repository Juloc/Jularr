using System.Security;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Browser.V1
{
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
        internal CloseBrowserAssistanceV1(BrowserAssistance assistance) => _assistance = assistance;
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BrowserLeaseParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Browser");
        protected override ServicePermission GetPermission(BrowserLeaseParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override async Task<IServiceOutput> OnExecute()
        {
            await _assistance.CloseAsync(Auth.SessionPublicId ?? throw new SecurityException(), Parameters.LeaseId, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
