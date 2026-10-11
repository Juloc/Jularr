using Jularr.Data.Common;
using Jularr.Data.Profile;
using Jularr.Data.User.Profile.V1;
using Jularr.Logic.Profile;
using Jularr.Service.Core;

namespace Jularr.Service.User.Profile.V1;

internal static class ProfileReadSql
{
    internal const string OwnProfile =
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
}

public sealed class GetProfile(ServiceRuntime runtime) : UserReadService<ProfileParameters>(runtime)
{
    protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
        [ServiceResultType.Default<ProfileDetails>()];

    protected override ModuleRequirement GetInstanceModules(ProfileParameters parameters) =>
        ModuleRequirement.None;

    protected override ServicePermission GetPermission(ProfileParameters parameters, Type resultType) =>
        new("profile.self.read");

    protected override ResourceTarget? GetResource(ProfileParameters parameters) => null;

    protected override void ValidateParameters(ProfileParameters parameters)
    {
        if (parameters.PublicId == Guid.Empty)
        {
            throw new ArgumentException("Profile public ID is required.", nameof(parameters));
        }
    }

    protected override async Task<IServiceOutput> ExecuteCoreAsync(
        ProfileParameters parameters,
        NoData data,
        Type resultType,
        ServiceContext context,
        CancellationToken cancellationToken) =>
        await context.ReadSql.ReadRequiredAsync<ProfileDetails>(
            ProfileReadSql.OwnProfile,
            new ProfileReadParameters(parameters.PublicId, context.Access.SessionPublicId!.Value, context.Access.SessionRevision!.Value),
            cancellationToken);
}

public sealed class RenameProfile(ServiceRuntime runtime)
    : UserService<ProfileParameters, RenameProfileData>(runtime)
{
    protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;

    protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
        [ServiceResultType.Default<ProfileDetails>()];

    protected override ModuleRequirement GetInstanceModules(ProfileParameters parameters) =>
        ModuleRequirement.None;

    protected override ServicePermission GetPermission(ProfileParameters parameters, Type resultType) =>
        new("profile.self.rename");

    protected override ResourceTarget? GetResource(ProfileParameters parameters) => null;

    protected override void ValidateParameters(ProfileParameters parameters)
    {
        if (parameters.PublicId == Guid.Empty)
        {
            throw new ArgumentException("Profile public ID is required.", nameof(parameters));
        }
    }

    protected override async Task<IServiceOutput> ExecuteCoreAsync(
        ProfileParameters parameters,
        RenameProfileData data,
        Type resultType,
        ServiceContext context,
        CancellationToken cancellationToken)
    {
        await context.Logic.RenameProfileAsync(
            parameters.PublicId,
            data.DisplayName,
            cancellationToken);

        return await context.ReadSql.ReadRequiredAsync<ProfileDetails>(
            ProfileReadSql.OwnProfile,
            new ProfileReadParameters(parameters.PublicId, context.Access.SessionPublicId!.Value, context.Access.SessionRevision!.Value),
            cancellationToken);
    }
}
