using Jularr.Data.Common;
using Jularr.Data.Profile;
using Jularr.Data.User.Profile.V1;
using Jularr.Logic.Profile;
using Jularr.Infrastructure.Sql;
using Jularr.Service.Core;

namespace Jularr.Service.User.Profile.V1;

public sealed class ReadProfileV1(ServiceRuntime runtime) : UserService<ProfileParameters>(runtime)
{
    protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
        [ServiceResultType.Default<ProfileDetails>()];

    protected override ModuleRequirement GetInstanceModules(ProfileParameters parameters) =>
        ModuleRequirement.None;

    protected override ServicePermission GetPermission(ProfileParameters parameters, Type resultType) =>
        new("profile.self.read");

    protected override void ValidateParameters(ProfileParameters parameters)
    {
        if (parameters.PublicId == Guid.Empty)
        {
            throw new ArgumentException("Profile public ID is required.", nameof(parameters));
        }
    }

    protected override Task ValidatePermissions(ProfileParameters parameters)
    {
        if (parameters.PublicId != Auth.ProfilePublicId)
        {
            throw new KeyNotFoundException("The authorized profile was not found.");
        }

        return Task.CompletedTask;
    }

    protected override async Task<IServiceOutput> OnRead()
    {
        var sql = new SqlStatement();
        sql.Statement =
            """
            SELECT
                profile."PublicId",
                profile."DisplayName"
            FROM
                "Profiles" AS profile
            WHERE
                profile."PublicId" = @PublicId
                AND profile."Id" = @ActiveProfileId
                AND EXISTS (
                    SELECT
                        1
                    FROM
                        "AccountProfiles" AS membership
                    INNER JOIN
                        "Accounts" AS account ON account."Id" = membership."AccountId"
                    INNER JOIN
                        "AccountSessions" AS session ON session."AccountId" = account."Id"
                            AND session."ActiveProfileId" = membership."ProfileId"
                    WHERE
                        membership."ProfileId" = profile."Id"
                        AND membership."AccountId" = @ActorAccountId
                        AND account."IsEnabled" = TRUE
                        AND session."PublicId" = @SessionPublicId
                        AND session."RotationRevision" = @SessionRevision
                        AND session."RevokedAt" IS NULL
                        AND session."ExpiresAt" > statement_timestamp()
                        AND session."CredentialRevision" = account."CredentialRevision"
                );
            """;
        sql.Parameters.Add(new ProfileReadParameters(Parameters.PublicId, Auth.SessionPublicId!.Value, Auth.SessionRevision!.Value));
        return await Context.ReadRequiredAsync<ProfileDetails>(sql, CancellationToken);
    }
}

public sealed class UpdateProfileV1(ServiceRuntime runtime)
    : UserService<ProfileParameters, UpdateProfileDataV1>(runtime)
{
    protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;

    protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
        [ServiceResultType.Default<UpdateResult>()];

    protected override ModuleRequirement GetInstanceModules(ProfileParameters parameters) =>
        ModuleRequirement.None;

    protected override ServicePermission GetPermission(ProfileParameters parameters, Type resultType) =>
        new("profile.self.update");

    protected override void ValidateParameters(ProfileParameters parameters)
    {
        if (parameters.PublicId == Guid.Empty)
        {
            throw new ArgumentException("Profile public ID is required.", nameof(parameters));
        }
    }

    protected override Task ValidatePermissions(ProfileParameters parameters)
    {
        if (parameters.PublicId != Auth.ProfilePublicId)
        {
            throw new KeyNotFoundException("The authorized profile was not found.");
        }

        if (Auth.ProfileOwnerAccountId != Auth.AccountId || Auth.AccountId is null)
        {
            throw new System.Security.SecurityException("Profile management requires ownership.");
        }

        return Task.CompletedTask;
    }

    protected override async Task<IServiceOutput> OnUpdate()
    {
        var updateData = await Context.ReadProfileForUpdateAsync(Parameters.PublicId, CancellationToken);
        updateData.SetData(new ProfileNameChange(Parameters.PublicId, Data.DisplayName));
        await Context.UpdateProfileAsync(updateData, CancellationToken);
        return new UpdateResult(true);
    }
}
