using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Profile;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Account.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/accounts/{accountId:long}/profiles")]
    public sealed class ReadAccountProfilesV1 : AdminService<ReadAccountProfilesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AccountProfileDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadAccountProfilesParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadAccountProfilesParametersV1 parameters, Type resultType) => new("accounts.read");
        protected override PageRequest GetPageRequest(ReadAccountProfilesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read actual account assignments.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId",
                    profile."DisplayName",
                    profile."OwnerAccountId",
                    (profile."PinHash" IS NOT NULL) AS "HasPin"
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
            sql.Parameters.Add(new AccountParameters(Parameters.AccountId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<AccountProfileDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/accounts/{accountId:long}/profiles/{profileId:long}")]
    public sealed class AddAccountProfileV1 : AdminService<AccountProfileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AccountProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AccountProfileParametersV1 parameters, Type resultType) => new("accounts.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            await Context.AddAccountProfileAsync(new AccountProfileData(Parameters.AccountId, Parameters.ProfileId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/accounts/{accountId:long}/profiles/{profileId:long}")]
    public sealed class RemoveAccountProfileV1 : AdminService<AccountProfileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AccountProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AccountProfileParametersV1 parameters, Type resultType) => new("accounts.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.RemoveAccountProfileAsync(new AccountProfileData(Parameters.AccountId, Parameters.ProfileId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/profiles/{profileId:long}/pin")]
    public sealed class ResetProfilePinV1 : AdminService<ProfilePinParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ProfilePinParametersV1 parameters) => ModuleRequirement.All("Profiles");
        protected override ServicePermission GetPermission(ProfilePinParametersV1 parameters, Type resultType) => new("accounts.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            // The Owner recovers a forgotten PIN by removing it.
            await Context.UpdateProfilePinAsync(Parameters.ProfileId, null, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
