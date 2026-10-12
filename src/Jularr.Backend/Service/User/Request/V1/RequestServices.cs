using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Request.V1
{
    [ServiceEndpoint("GET", "/api/v1/requests")]
    public sealed class ReadRequestsV1 : UserService<ReadRequestsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<RequestDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadRequestsParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadRequestsParametersV1 parameters, Type resultType) => new("profile.requests.read");
        protected override PageRequest GetPageRequest(ReadRequestsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Own profile, one bounded read.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    request."Id",
                    request."WorkId",
                    work."CanonicalTitle",
                    locale."Name" AS "RequestedLanguage",
                    request."AcquisitionRequestStatusTypeId",
                    request."CreatedAt"
                FROM
                    "AcquisitionRequests" AS request
                INNER JOIN
                    "Works" AS work ON work."Id" = request."WorkId"
                INNER JOIN
                    "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                WHERE
                    request."RequestedByProfileId" = @ProfileId
                ORDER BY
                    request."CreatedAt" DESC,
                    request."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<RequestDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/works/{workId:long}/requests")]
    public sealed class CreateRequestV1 : UserService<CreateRequestParametersV1, CreateRequestDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateRequestResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateRequestParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(CreateRequestParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var request = await Context.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(Parameters.WorkId, Auth.ProfileId!.Value, Data.UiLocaleId), CancellationToken);
            return new CreateRequestResultV1(request.Id);
        }
    }
}
