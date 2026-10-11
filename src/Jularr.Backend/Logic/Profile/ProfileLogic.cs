using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Infrastructure.Sql;
using Npgsql;

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
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountProfiles" ("AccountId", "ProfileId")
                VALUES
                    (@AccountId, @ProfileId);
                """;
            sql.Parameters.Add(new AccountProfileData(data.OwnerAccountId, profile.Id));
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new InvalidOperationException("The profile membership could not be created.");
            }
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

            var sql = new SqlStatement();
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

            // Translate reference failures.
            int affected;
            try
            {
                affected = await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && exception.ConstraintName == "FK_Profiles_UiLocales")
            {
                throw new DataValidationException(nameof(updateData.UiLocaleId), "unknown_reference");
            }

            if (affected != 1)
            {
                throw new KeyNotFoundException("The authorized profile was not found.");
            }
        }
    }
}
