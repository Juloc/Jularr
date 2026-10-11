using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Profile.V1
{
    [ServiceEndpoint("GET", "/api/v1/profiles")]
    public sealed class ReadProfilesV1 : UserService<ReadProfilesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<ProfileSelectionDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadProfilesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadProfilesParametersV1 parameters, Type resultType) => new("session.profile.read");
        protected override PageRequest GetPageRequest(ReadProfilesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read selectable memberships only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."DisplayName",
                    (profile."PinHash" IS NOT NULL) AS "IsLocked"
                FROM
                    "AccountProfiles" AS membership
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = membership."ProfileId"
                WHERE
                    membership."AccountId" = @AccountId
                ORDER BY
                    profile."DisplayName",
                    profile."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new SessionAccountParameters(Auth.AccountId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ProfileSelectionDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/auth/profile/{profileId:long}")]
    public sealed class SelectProfileV1 : UserService<SelectProfileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SelectProfileResultV1>()];
        protected override ModuleRequirement GetInstanceModules(SelectProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SelectProfileParametersV1 parameters, Type resultType) => new("session.profile.select");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Rotate the verified session context.
            var proof = new SessionParameters(Auth.SessionPublicId!.Value, Auth.SessionRevision!.Value, Auth.SessionTokenHash!);
            var target = new ProfileMembershipParameters(Auth.AccountId!.Value, Parameters.ProfileId);
            var session = await Context.SelectSessionProfileAsync(proof, target, CancellationToken);
            return new SelectProfileResultV1(session);
        }
    }
}
