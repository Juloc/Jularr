using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
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
                    request."CreatedAt",
                    waiting."OperationId",
                    waiting."ManualUrl"
                FROM
                    "AcquisitionRequests" AS request
                INNER JOIN
                    "Works" AS work ON work."Id" = request."WorkId"
                INNER JOIN
                    "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                LEFT JOIN LATERAL (
                    SELECT
                        operation."Id" AS "OperationId",
                        operation."AttentionUrl" AS "ManualUrl"
                    FROM
                        "RequestTargets" AS target
                    INNER JOIN
                        "AcquisitionReleases" AS release ON release."WantedItemId" = target."WantedItemId"
                    INNER JOIN
                        "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id" AND operation."OperationTypeId" = 2
                    INNER JOIN
                        "InstanceSettings" AS settings ON settings."UsersConfirmSources"
                    INNER JOIN
                        "Accounts" AS account ON account."Id" = @AccountId AND account."MayConfirmSources"
                    WHERE
                        target."AcquisitionRequestId" = request."Id"
                        AND operation."OperationStatusTypeId" IN (1, 2)
                        AND operation."AttentionUrl" IS NOT NULL
                    ORDER BY
                        operation."Id" DESC
                    LIMIT 1
                ) AS waiting ON TRUE
                WHERE
                    request."RequestedByProfileId" = @ProfileId
                ORDER BY
                    request."CreatedAt" DESC,
                    request."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Parameters.Add(new AccountParameters(Auth.AccountId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<RequestDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/requests/{acquisitionRequestId:long}/source-link")]
    public sealed class SaveRequestSourceLinkV1 : UserService<RequestParametersV1, SaveRequestSourceLinkDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(RequestParametersV1 parameters) => ModuleRequirement.All("Requests", "Acquisition");
        protected override ServicePermission GetPermission(RequestParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SetSourceLinkAsync(new SourceLinkData(Data.OperationId, Data.SourceLinkUrl, Parameters.AcquisitionRequestId, Auth.ProfileId!.Value, Auth.AccountId!.Value), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
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
            var request = await Context.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(Parameters.WorkId, Auth.ProfileId!.Value, Data.UiLocaleId, Data.GamePlatformId), CancellationToken);
            return new CreateRequestResultV1(request.Id);
        }
    }
}
