using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Localization;

namespace Jularr.Backend.Logic.Profile
{
    internal static class ProfileLogic
    {
        public static async Task CreateOwnedProfileAsync(this LogicContext ctx, ProfileCreationData data, CancellationToken ct)
        {
            // An account owns at most ten profiles; its row serializes concurrent creations.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id"
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @OwnerAccountId
                FOR UPDATE OF
                    account;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadRequiredAsync<AccountIdData>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id"
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @OwnerAccountId
                    AND (SELECT count(*) FROM "Profiles" AS profile WHERE profile."OwnerAccountId" = account."Id") < 10;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<AccountIdData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.DisplayName), "profile_limit");
            }

            await ctx.CreateProfileAsync(data, ct);
        }

        public static async Task DeleteProfileAsync(this LogicContext ctx, AccountProfileData data, CancellationToken ct)
        {
            // Only the owning account deletes a profile.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                    AND profile."OwnerAccountId" = @AccountId
                FOR UPDATE OF
                    profile;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<ProfileParameters>(sql, ct) is null)
            {
                throw new KeyNotFoundException("The profile was not found.");
            }

            // Sessions on the profile return to the picker; its open requests are cancelled, the others stay with the account; sign-in flows and links go with it.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "ActiveProfileId" = NULL
                WHERE
                    session."ActiveProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionRequests" AS request
                SET
                    "AcquisitionRequestStatusTypeId" = 9,
                    "UpdatedAt" = now(),
                    "Version" = request."Version" + 1
                WHERE
                    request."RequestedByProfileId" = @ProfileId
                    AND request."AcquisitionRequestStatusTypeId" = 1;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AccountExternalAuthFlows" AS flow
                WHERE
                    flow."StartedProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AccountProfiles" AS accountProfile
                WHERE
                    accountProfile."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
        public static async Task CreateProfileAsync(this LogicContext ctx, ProfileCreationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
                SELECT
                    @OwnerAccountId,
                    @DisplayName,
                    locale."Id"
                FROM
                    "UiLocales" AS locale
                WHERE
                    locale."Id" = @UiLocaleId
                    AND locale."IsEnabled" = TRUE
                RETURNING
                    "Id",
                    "DisplayName",
                    "UiLocaleId";
                """;
            sql.Parameters.Add(data);

            var profile = await ctx.ReadOptionalAsync<ProfileUpdateData>(sql, ct)
                ?? throw new DataValidationException(nameof(data.UiLocaleId), "unknown_reference");

            // Establish ownership before commit.
            await ctx.AddAccountProfileAsync(new AccountProfileData(data.OwnerAccountId, profile.Id), ct);

            // The first owned profile becomes this account's main PWA profile; choosing another one is explicit.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "Accounts"
                SET "PwaMainProfileId" = @ProfileId
                WHERE "Id" = @AccountId AND "PwaMainProfileId" IS NULL;
                """;
            sql.Parameters.Add(new AccountProfileData(data.OwnerAccountId, profile.Id));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<ProfileUpdateData> ReadProfileAsync(this LogicContext ctx, long profileId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."DisplayName",
                    profile."UiLocaleId",
                    profile."FirstContentLanguage",
                    profile."SecondContentLanguage"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                FOR UPDATE OF
                    profile;
                """;
            sql.Parameters.Add(new ProfileParameters(profileId));

            return ctx.ReadRequiredAsync<ProfileUpdateData>(sql, ct);
        }

        // One change per profile at a time where a limit or a whole list is checked.
        public static async Task LockProfileAsync(this LogicContext ctx, long profileId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                FOR UPDATE OF
                    profile;
                """;
            sql.Parameters.Add(new ProfileParameters(profileId));
            _ = await ctx.ReadRequiredAsync<ProfileParameters>(sql, ct);
        }

        public static async Task UpdateProfileAsync(this LogicContext ctx, ProfileUpdateData updateData, CancellationToken ct)
        {
            await ctx.RequireEnabledUiLocaleAsync(updateData.UiLocaleId, nameof(updateData.UiLocaleId), ct);

            if (updateData.SecondContentLanguage is not null && (updateData.FirstContentLanguage is null || updateData.FirstContentLanguage == updateData.SecondContentLanguage))
            {
                throw new DataValidationException(nameof(updateData.SecondContentLanguage), "invalid_value");
            }

            // Write the merged profile.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Profiles" AS profile
                SET
                    "DisplayName" = @DisplayName,
                    "UiLocaleId" = @UiLocaleId,
                    "FirstContentLanguage" = @FirstContentLanguage,
                    "SecondContentLanguage" = @SecondContentLanguage,
                    "UpdatedAt" = now()
                WHERE
                    profile."Id" = @Id;
                """;
            sql.Parameters.Add(updateData);

            var affected = await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            if (affected != 1)
            {
                throw new KeyNotFoundException("The authorized profile was not found.");
            }
        }
    }
}
