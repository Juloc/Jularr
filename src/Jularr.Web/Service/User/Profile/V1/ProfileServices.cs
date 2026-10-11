using Jularr.Data.Common;
using Jularr.Data.User.Profile.V1;
using Jularr.Logic.Profile;
using Jularr.Service.Core;

namespace Jularr.Service.User.Profile.V1;

internal static class ProfileReadSql
{
    internal const string OwnProfile =
        """
        SELECT profile."PublicId",
               profile."DisplayName"
        FROM "Profiles" AS profile
        WHERE profile."PublicId" = @PublicId
          AND profile."Id" = @ActiveProfileId
          AND EXISTS (
              SELECT 1
              FROM "AccountProfiles" AS membership
              JOIN "Accounts" AS account
                ON account."Id" = membership."AccountId"
              WHERE membership."ProfileId" = profile."Id"
                AND membership."AccountId" = @ActorAccountId
                AND account."IsEnabled" = true
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

    // The client provides a public UUID. There is no trusted internal bigint yet;
    // SQL resolves it only within the authenticated active Profile and Account.
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
            parameters,
            cancellationToken);
}

public sealed class RenameProfile(ServiceRuntime runtime, ProfileLogic logic)
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
        await logic.RenameAsync(
            context.Logic,
            parameters.PublicId,
            data.DisplayName,
            cancellationToken);

        // This SELECT participates in the SAME Service-owned RW transaction.
        // No extra connection, repository, Store or direct UI mutation.
        return await context.ReadSql.ReadRequiredAsync<ProfileDetails>(
            ProfileReadSql.OwnProfile,
            parameters,
            cancellationToken);
    }
}
