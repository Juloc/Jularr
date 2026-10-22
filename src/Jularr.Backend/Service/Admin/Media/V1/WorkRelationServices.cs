using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // An administrator relates a Work to another Work or to a title of Discover's catalog; the other Work shows the inverse.
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/relations")]
    public sealed class CreateWorkRelationV1 : AdminService<WorkParametersV1, CreateWorkRelationDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "Franchises");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            await Context.AddWorkRelationAsync(new WorkRelationAddData(Parameters.WorkId, Data.WorkRelationTypeId, Data.RelatedWorkId, Data.Namespace, Data.ExternalId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // A relation of a person goes; one of a provider is hidden and stays hidden after the next sync.
    [ServiceEndpoint("DELETE", "/api/v1/admin/works/{workId:long}/relations/{workRelationId:long}")]
    public sealed class DeleteWorkRelationV1 : AdminService<WorkRelationParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkRelationParametersV1 parameters) => ModuleRequirement.All("Library", "Franchises");
        protected override ServicePermission GetPermission(WorkRelationParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.RemoveWorkRelationAsync(new WorkRelationRemoveData(Parameters.WorkId, Parameters.WorkRelationId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
