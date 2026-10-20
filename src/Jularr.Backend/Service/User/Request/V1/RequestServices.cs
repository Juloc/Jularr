using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Enums.Account;
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
                    visible."Title" AS "CanonicalTitle",
                    locale."Name" AS "RequestedLanguage",
                    request."AcquisitionRequestStatusTypeId",
                    request."CreatedAt",
                    waiting."OperationId",
                    waiting."ManualUrl",
                    step."ActivityEventTypeId" AS "LatestStepTypeId",
                    step."OccurredAt" AS "LatestStepAt",
                    (
                        work."MediaTypeId" = 3
                        AND work."OriginalLanguage" IS NOT NULL
                        AND work."OriginalLanguage" <> lower(split_part(locale."Locale", '-', 1))
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkEditions" AS edition
                            WHERE
                                edition."WorkId" = work."Id"
                                AND edition."LanguageCode" = lower(split_part(locale."Locale", '-', 1))
                        )
                    ) AS "ReadsTranslated",
                    work."MediaTypeId",
                    visible."MediaSubtypeId",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = work."Id"
                            AND fact."FactKey" = 'Poster'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "PosterUrl",
                    quality."Name" AS "QualityName",
                    request."AcquisitionRequestStatusTypeId" IN (1, 2, 3, 4, 5) AS "IsWithdrawable"
                FROM
                    "AcquisitionRequests" AS request
                INNER JOIN
                    "ProfileWorks" AS visible ON visible."WorkId" = request."WorkId" AND visible."ProfileId" = request."RequestedByProfileId"
                INNER JOIN
                    "Works" AS work ON work."Id" = request."WorkId"
                INNER JOIN
                    "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
                LEFT JOIN
                    "AcquisitionProfiles" AS quality ON quality."Id" = request."AcquisitionProfileId"
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
                LEFT JOIN LATERAL (
                    SELECT
                        event."ActivityEventTypeId",
                        event."OccurredAt"
                    FROM
                        "ActivityEvents" AS event
                    WHERE
                        event."WorkId" = request."WorkId"
                        AND event."ActivityEventTypeId" IN (1, 2, 3, 4)
                        AND event."OccurredAt" >= request."CreatedAt"
                    ORDER BY
                        event."OccurredAt" DESC,
                        event."Id" DESC
                    LIMIT 1
                ) AS step ON TRUE
                WHERE
                    request."RequestedByProfileId" = @ProfileId
                    AND (@MediaTypeId::smallint IS NULL OR work."MediaTypeId" = @MediaTypeId AND (@MediaSubtypeId::smallint IS NOT NULL OR visible."MediaSubtypeId" IS NULL))
                    AND (@MediaSubtypeId::smallint IS NULL OR visible."MediaSubtypeId" = @MediaSubtypeId)
                    AND CASE @RequestListFilterTypeId::smallint
                        WHEN 1 THEN request."AcquisitionRequestStatusTypeId" = 1
                        WHEN 2 THEN request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 5
                        WHEN 3 THEN request."AcquisitionRequestStatusTypeId" = 6
                        WHEN 4 THEN request."AcquisitionRequestStatusTypeId" = 8
                        WHEN 5 THEN request."AcquisitionRequestStatusTypeId" IN (6, 7, 9)
                        ELSE TRUE
                    END
                ORDER BY
                    CASE WHEN @RequestSortTypeId = 3 THEN
                        CASE request."AcquisitionRequestStatusTypeId" WHEN 8 THEN 0 WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 3 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 6 THEN 6 ELSE 7 END
                    END,
                    CASE WHEN @RequestSortTypeId = 4 THEN lower(visible."Title") END,
                    CASE WHEN @RequestSortTypeId = 2 THEN request."CreatedAt" END,
                    CASE WHEN @RequestSortTypeId = 2 THEN request."Id" END,
                    request."CreatedAt" DESC,
                    request."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Parameters.Add(new AccountParameters(Auth.AccountId!.Value));
            sql.Parameters.Add(new ProfileRequestFilterData(Parameters.RequestListFilterTypeId, Parameters.RequestSortTypeId, Parameters.MediaTypeId, Parameters.MediaSubtypeId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<RequestDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/requests/{acquisitionRequestId:long}/steps")]
    public sealed class ReadRequestStepsV1 : UserService<RequestParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<RequestStepV1>>()];
        protected override ModuleRequirement GetInstanceModules(RequestParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(RequestParametersV1 parameters, Type resultType) => new("profile.requests.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's own request, the steps since it was made, the latest 50.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    event."ActivityEventTypeId",
                    event."OccurredAt",
                    event."WorkUnitTypeId",
                    event."ParentUnitNumber",
                    event."UnitNumber",
                    event."QualityKey"
                FROM
                    "AcquisitionRequests" AS request
                    JOIN "ActivityEvents" AS event ON event."WorkId" = request."WorkId" AND event."OccurredAt" >= request."CreatedAt"
                WHERE
                    request."Id" = @AcquisitionRequestId
                    AND request."RequestedByProfileId" = @ProfileId
                    AND event."ActivityEventTypeId" IN (1, 2, 3, 4)
                ORDER BY
                    event."OccurredAt" DESC,
                    event."Id" DESC
                LIMIT 50;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<RequestStepV1>(await Context.ReadSql.ReadListAsync<RequestStepV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/requests/{acquisitionRequestId:long}/withdraw")]
    public sealed class WithdrawRequestV1 : UserService<RequestParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(RequestParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(RequestParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Own requests only, until they are imported.
            await Context.WithdrawAcquisitionRequestAsync(new AcquisitionRequestWithdrawData(Parameters.AcquisitionRequestId, Auth.ProfileId!.Value), CancellationToken);
            return new UpdateResult(true);
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

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/request-profiles")]
    public sealed class ReadRequestProfilesV1 : UserService<CreateRequestParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<RequestProfileOptionV1>>()];
        protected override ModuleRequirement GetInstanceModules(CreateRequestParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(CreateRequestParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The qualities the account's rule offers for this Work's type and subtype.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Name"
                FROM
                    "EffectiveRequestRules" AS rule
                    JOIN "AcquisitionProfiles" AS profile ON profile."Id" = ANY(rule."AcquisitionProfileIds")
                    JOIN "WorkClasses" AS class ON class."MediaTypeId" = profile."MediaTypeId" AND class."MediaSubtypeId" IS NOT DISTINCT FROM profile."MediaSubtypeId"
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = class."WorkId" AND visible."ProfileId" = @ProfileId
                WHERE
                    rule."AccountId" = @AccountId
                    AND class."WorkId" = @WorkId
                ORDER BY
                    profile."Name";
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new AccountProfileData(Auth.AccountId!.Value, Auth.ProfileId!.Value));
            return new BaseDataList<RequestProfileOptionV1>(await Context.ReadSql.ReadListAsync<RequestProfileOptionV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/works/{workId:long}/requests")]
    public sealed class CreateRequestV1 : UserService<CreateRequestParametersV1, CreateRequestDataV1>
    {
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _newlyApproved;

        internal CreateRequestV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateRequestResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateRequestParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(CreateRequestParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var (request, newlyApproved) = await Context.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(Parameters.WorkId, Auth.ProfileId!.Value, Auth.AccountId!.Value, Data.UiLocaleId, Data.GamePlatformId, Data.AcquisitionProfileId, Auth.AccountRoleTypeId == AccountRoleType.Owner), CancellationToken);
            _newlyApproved = newlyApproved;
            return new CreateRequestResultV1(request.Id);
        }

        protected override void OnCommitted()
        {
            if (_newlyApproved) _searchSignal.RequestSearch();
        }
    }
}
