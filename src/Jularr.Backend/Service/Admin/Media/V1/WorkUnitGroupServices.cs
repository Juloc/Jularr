using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/works/{workId:long}/unit-groups")]
    public sealed class ReadWorkUnitGroupsV1 : AdminService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkUnitGroupV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "UnitGroups");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // At most 100 per Work, in unit order.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    unitGroup."Id",
                    unitGroup."Name",
                    unitGroup."FirstParentNumber",
                    unitGroup."FirstNumber",
                    unitGroup."LastParentNumber",
                    unitGroup."LastNumber"
                FROM
                    "WorkUnitGroups" AS unitGroup
                WHERE
                    unitGroup."WorkId" = @WorkId
                ORDER BY
                    coalesce(unitGroup."FirstParentNumber", 0),
                    unitGroup."FirstNumber"
                LIMIT 100;
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WorkUnitGroupV1>(await Context.ReadSql.ReadListAsync<WorkUnitGroupV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/unit-groups")]
    public sealed class CreateWorkUnitGroupV1 : AdminService<WorkParametersV1, CreateWorkUnitGroupDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateWorkUnitGroupResultV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "UnitGroups");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnCreate() =>
            new CreateWorkUnitGroupResultV1(await Context.CreateWorkUnitGroupAsync(new WorkUnitGroupSaveData(Parameters.WorkId, Data.Name, Data.FirstParentNumber, Data.FirstNumber,
                Data.LastParentNumber, Data.LastNumber), CancellationToken));
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/works/{workId:long}/unit-groups/{workUnitGroupId:long}")]
    public sealed class DeleteWorkUnitGroupV1 : AdminService<WorkUnitGroupParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkUnitGroupParametersV1 parameters) => ModuleRequirement.All("Library", "UnitGroups");
        protected override ServicePermission GetPermission(WorkUnitGroupParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteWorkUnitGroupAsync(new WorkUnitGroupKeyData(Parameters.WorkId, Parameters.WorkUnitGroupId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
