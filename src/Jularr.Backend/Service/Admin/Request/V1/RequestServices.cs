using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Request.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/requests")]
    public sealed class ReadRequestsV1 : AdminService<ReadRequestsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<RequestDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadRequestsParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadRequestsParametersV1 parameters, Type resultType) => new("requests.read");
        protected override PageRequest GetPageRequest(ReadRequestsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // No navigation counts or facets.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    request."Id",
                    request."WorkId",
                    work."CanonicalTitle",
                    work."MediaTypeId",
                    COALESCE(profile."DisplayName", account."DisplayName") AS "RequesterName",
                    locale."Name" AS "RequestedLanguage",
                    request."AcquisitionRequestStatusTypeId",
                    request."Version",
                    request."CreatedAt"
                FROM
                    "AcquisitionRequests" AS request
                INNER JOIN
                    "Works" AS work ON work."Id" = request."WorkId"
                INNER JOIN
                    "Accounts" AS account ON account."Id" = request."RequestedByAccountId"
                LEFT JOIN
                    "Profiles" AS profile ON profile."Id" = request."RequestedByProfileId"
                INNER JOIN
                    "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                WHERE
                    @RequestTabTypeId = 0
                    OR CASE
                        WHEN request."AcquisitionRequestStatusTypeId" = 1 THEN 1
                        WHEN request."AcquisitionRequestStatusTypeId" = 2 THEN 2
                        WHEN request."AcquisitionRequestStatusTypeId" = 6 THEN 4
                        WHEN request."AcquisitionRequestStatusTypeId" IN (7, 9) THEN 5
                        ELSE 3
                    END = @RequestTabTypeId
                ORDER BY
                    request."CreatedAt" DESC,
                    request."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new AcquisitionRequestFilterData(Parameters.RequestTabTypeId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<RequestDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/requests/{acquisitionRequestId:long}")]
    public sealed class UpdateRequestV1 : AdminService<UpdateRequestParametersV1, UpdateRequestDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;

        internal UpdateRequestV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateRequestParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(UpdateRequestParametersV1 parameters, Type resultType) => new("requests.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateAcquisitionRequestAsync(new AcquisitionRequestUpdateData(Parameters.AcquisitionRequestId, Data.AcquisitionRequestStatusTypeId, Data.Version, Auth.AccountId!.Value), CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted()
        {
            if (Data.AcquisitionRequestStatusTypeId == AcquisitionRequestStatusType.Approved) _searchSignal.RequestSearch();
        }
    }
}
