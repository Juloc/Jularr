using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Profile.V1
{
    [ServiceEndpoint("GET", "/api/v1/profile/pwa")]
    public sealed class ReadPwaAppearanceV1 : UserService<PwaAppearanceParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PwaAppearanceV1>()];
        protected override ModuleRequirement GetInstanceModules(PwaAppearanceParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PwaAppearanceParametersV1 parameters, Type resultType) => new("session.profile.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // No active-profile dependency: this selection stays fixed when switching among profiles.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."PwaMainProfileId" AS "MainProfileId",
                    coalesce(profile."AccentHex", '#8040f7') AS "AccentHex"
                FROM "Accounts" AS account
                LEFT JOIN "Profiles" AS profile ON profile."Id" = account."PwaMainProfileId"
                WHERE account."Id" = @AccountId;
                """;
            sql.Parameters.Add(new AccountParameters(Auth.AccountId!.Value));
            return await Context.ReadRequiredAsync<PwaAppearanceV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/profile/pwa/main/{profileId:long}")]
    public sealed class SetPwaMainProfileV1 : UserService<SelectProfileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SelectProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SelectProfileParametersV1 parameters, Type resultType) => new("session.profile.select");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "Accounts" AS account
                SET "PwaMainProfileId" = @ProfileId
                WHERE account."Id" = @AccountId
                  AND EXISTS (
                    SELECT 1
                    FROM "AccountProfiles" AS link
                    WHERE link."AccountId" = account."Id"
                      AND link."ProfileId" = @ProfileId
                  );
                """;
            sql.Parameters.Add(new AccountProfileData(Auth.AccountId!.Value, Parameters.ProfileId));
            if (await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken) != 1)
                throw new KeyNotFoundException("The profile is not assigned to this account.");
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/profiles/{profileId:long}/pwa/accent")]
    public sealed class SavePwaAccentV1 : UserService<SelectProfileParametersV1, SavePwaAccentDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SelectProfileParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SelectProfileParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override Task ValidatePermissions(SelectProfileParametersV1 parameters) =>
            parameters.ProfileId == Auth.ProfileId ? Task.CompletedTask
                : throw new KeyNotFoundException("The authorized profile was not found.");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var color = Data.AccentHex.ToLowerInvariant();
            if (color.Length != 7 || color[0] != '#' || color.AsSpan(1).IndexOfAnyExcept("0123456789abcdef") >= 0)
                throw new DataValidationException(nameof(Data.AccentHex), "invalid_text");

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "Profiles" SET "AccentHex" = @AccentHex, "UpdatedAt" = now()
                WHERE "Id" = @ProfileId;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(new SavePwaAccentDataV1(color));
            if (await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken) != 1)
                throw new KeyNotFoundException("The authorized profile was not found.");
            return new UpdateResult(true);
        }

    }
    // The visible account accent is the owned main profile's stored color.
    // Selecting a shared profile for the launcher does not grant permission to recolor somebody else's profile.
    [ServiceEndpoint("PUT", "/api/v1/profile/pwa/accent")]
    public sealed class SaveMainPwaAccentV1 : UserService<PwaAppearanceParametersV1, SavePwaAccentDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PwaAppearanceParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PwaAppearanceParametersV1 parameters, Type resultType) => new("session.profile.select");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var accent = Data.AccentHex.ToLowerInvariant();
            if (accent.Length != 7 || accent[0] != '#' || accent.AsSpan(1).IndexOfAnyExcept("0123456789abcdef") >= 0)
                throw new DataValidationException(nameof(Data.AccentHex), "invalid_text");

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "Profiles" AS profile
                SET "AccentHex" = @AccentHex, "UpdatedAt" = now()
                FROM "Accounts" AS account
                WHERE account."Id" = @AccountId
                  AND account."PwaMainProfileId" = profile."Id"
                  AND profile."OwnerAccountId" = account."Id";
                """;
            sql.Parameters.Add(new AccountParameters(Auth.AccountId!.Value));
            sql.Parameters.Add(new SavePwaAccentDataV1(accent));
            if (await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken) != 1)
                throw new KeyNotFoundException("No owned main profile is available for this account.");
            return new UpdateResult(true);
        }
    }

}
