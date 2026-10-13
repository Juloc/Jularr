using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Logic.Profile;

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
            // Read linked profiles only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."DisplayName",
                    (profile."PinHash" IS NOT NULL) AS "IsLocked",
                    profile."PinLength",
                    (profile."OwnerAccountId" = @AccountId) AS "IsOwned"
                FROM
                    "AccountProfiles" AS accountProfile
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = accountProfile."ProfileId"
                WHERE
                    accountProfile."AccountId" = @AccountId
                ORDER BY
                    profile."DisplayName",
                    profile."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new AccountParameters(Auth.AccountId!.Value));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ProfileSelectionDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/auth/profiles")]
    public sealed class CreateProfileV1 : UserService<ReadProfilesParametersV1, CreateProfileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadProfilesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadProfilesParametersV1 parameters, Type resultType) => new("session.profile.create");

        protected override async Task<IServiceOutput> OnCreate()
        {
            await Context.CreateOwnedProfileAsync(new ProfileCreationData(Auth.AccountId!.Value, Data.DisplayName, Data.UiLocaleId), CancellationToken);
            return new UpdateResult(true);
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
            var target = new AccountProfileData(Auth.AccountId!.Value, Parameters.ProfileId);
            var session = await Context.SelectSessionProfileAsync(proof, target, null, CancellationToken);
            return new SelectProfileResultV1(session!);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/auth/profile/{profileId:long}/pin")]
    public sealed class VerifyProfilePinV1 : UserService<SelectProfileParametersV1, VerifyProfilePinDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SelectProfileResultV1>()];
        protected override ModuleRequirement GetInstanceModules(SelectProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SelectProfileParametersV1 parameters, Type resultType) => new("session.profile.select");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // A failed attempt is committed before it is reported.
            var proof = new SessionParameters(Auth.SessionPublicId!.Value, Auth.SessionRevision!.Value, Auth.SessionTokenHash!);
            var target = new AccountProfileData(Auth.AccountId!.Value, Parameters.ProfileId);
            var session = await Context.ExecuteBatchAsync((ctx, ct) => ctx.SelectSessionProfileAsync(proof, target, Data.Pin, ct), CancellationToken);
            return session is null ? throw new DataValidationException(nameof(VerifyProfilePinDataV1.Pin), "invalid_pin") : new SelectProfileResultV1(session);
        }
    }
}
