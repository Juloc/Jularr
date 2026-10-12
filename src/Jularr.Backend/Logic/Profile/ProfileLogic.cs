using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;

namespace Jularr.Backend.Logic.Profile
{
    internal static class ProfileLogic
    {
        public static async Task CreateProfileAsync(this LogicContext context, ProfileCreationData data, CancellationToken cancellationToken)
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

            ProfileUpdateData profile;
            try
            {
                profile = await context.ReadRequiredAsync<ProfileUpdateData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                throw new DataValidationException(nameof(data.UiLocaleId), "unknown_reference");
            }

            // Establish ownership before commit.
            await context.AddAccountProfileAsync(new AccountProfileData(data.OwnerAccountId, profile.Id), cancellationToken);
        }

        public static Task<ProfileUpdateData> ReadProfileAsync(this LogicContext context, long profileId, CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."DisplayName",
                    profile."UiLocaleId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                FOR UPDATE OF
                    profile;
                """;
            sql.Parameters.Add(new ProfileParameters(profileId));

            return context.ReadRequiredAsync<ProfileUpdateData>(sql, cancellationToken);
        }

        public static async Task UpdateProfileAsync(this LogicContext context, ProfileUpdateData updateData, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(updateData);

            // Validate the merged entity.
            updateData.Validate();

            // Lock the enabled language.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    locale."Id"
                FROM
                    "UiLocales" AS locale
                WHERE
                    locale."Id" = @UiLocaleId
                    AND locale."IsEnabled" = TRUE
                FOR SHARE OF
                    locale;
                """;
            sql.Parameters.Add(updateData);
            if (await context.ReadOptionalAsync<UiLocaleData>(sql, cancellationToken) is null)
            {
                throw new DataValidationException(nameof(updateData.UiLocaleId), "unknown_reference");
            }

            // Write the merged profile.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Profiles" AS profile
                SET
                    "DisplayName" = @DisplayName,
                    "UiLocaleId" = @UiLocaleId,
                    "UpdatedAt" = now()
                WHERE
                    profile."Id" = @Id;
                """;
            sql.Parameters.Add(updateData);

            var affected = await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);
            if (affected != 1)
            {
                throw new KeyNotFoundException("The authorized profile was not found.");
            }
        }
    }
}
