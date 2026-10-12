using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Logic.Account
{
    internal static class AccountLogic
    {
        private static readonly PasswordHasher<AccountAuthenticationData> PasswordHasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 600_000 }));
        private static readonly AccountAuthenticationData MissingAccount = new(1, 1, null, false, false);
        private static readonly string MissingPasswordHash = PasswordHasher.HashPassword(MissingAccount, Guid.NewGuid().ToString());

        public static Task<AccountUpdateData> ReadAccountAsync(this LogicContext context, long accountId, CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id",
                    account."DisplayName",
                    account."AccountRoleTypeId",
                    account."IsEnabled",
                    account."UpdatedAt"
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @AccountId
                FOR UPDATE OF account;
                """;
            sql.Parameters.Add(new AccountParameters(accountId));
            return context.ReadRequiredAsync<AccountUpdateData>(sql, cancellationToken);
        }

        public static async Task UpdateAccountAsync(this LogicContext context, AccountUpdateData data, DateTime expectedVersion, CancellationToken cancellationToken)
        {
            data.Validate();
            if (data.UpdatedAt != expectedVersion)
            {
                throw new DataValidationException(nameof(data.UpdatedAt), "outdated_version");
            }

            // Retain an enabled Owner.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH changed AS (
                    UPDATE
                        "Accounts" AS account
                    SET
                        "DisplayName" = @DisplayName,
                        "AccountRoleTypeId" = @AccountRoleTypeId,
                        "IsEnabled" = @IsEnabled,
                        "CredentialRevision" = account."CredentialRevision" + CASE
                            WHEN account."AccountRoleTypeId" <> @AccountRoleTypeId OR account."IsEnabled" <> @IsEnabled THEN 1
                            ELSE 0
                        END,
                        "UpdatedAt" = clock_timestamp()
                    WHERE
                        account."Id" = @Id
                        AND (account."AccountRoleTypeId" <> 1 OR (@AccountRoleTypeId = 1 AND @IsEnabled)
                            OR EXISTS (
                                SELECT 1
                                FROM "Accounts" AS owner
                                WHERE owner."Id" <> @Id
                                    AND owner."AccountRoleTypeId" = 1
                                    AND owner."IsEnabled" = TRUE
                            ))
                    RETURNING
                        account."Id",
                        account."CredentialRevision"
                ), revoked AS (
                    UPDATE
                        "AccountSessions" AS session
                    SET
                        "RevokedAt" = statement_timestamp()
                    FROM
                        changed
                    WHERE
                        session."AccountId" = changed."Id"
                        AND session."CredentialRevision" <> changed."CredentialRevision"
                        AND session."RevokedAt" IS NULL
                    RETURNING
                        session."Id"
                )
                SELECT
                    changed."Id"
                FROM
                    changed;
                """;
            sql.Parameters.Add(data);
            if (await context.ReadOptionalAsync<AccountIdData>(sql, cancellationToken) is null)
            {
                throw new DataValidationException(nameof(data.AccountRoleTypeId), "invalid_transition");
            }
        }

        public static async Task<AccountAuthenticationData> AuthenticatePasswordAsync(
            this LogicContext context,
            AccountEmailParameters parameters,
            string password,
            CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id",
                    account."CredentialRevision",
                    password."PasswordHash",
                    account."IsEnabled",
                    EXISTS (
                        SELECT 1
                        FROM "AccountTotpFactors" AS factor
                        WHERE factor."AccountId" = account."Id"
                            AND factor."ConfirmedAt" IS NOT NULL
                            AND factor."RevokedAt" IS NULL
                    ) AS "RequiresSecondFactor"
                FROM
                    "Accounts" AS account
                INNER JOIN
                    "AccountPasswords" AS password ON password."AccountId" = account."Id"
                WHERE
                    account."Email" = @Email
                FOR UPDATE OF
                    account, password;
                """;
            sql.Parameters.Add(parameters);

            var account = await context.ReadOptionalAsync<AccountAuthenticationData>(sql, cancellationToken);

            // Equal work for unknown accounts.
            var verification = PasswordHasher.VerifyHashedPassword(
                account ?? MissingAccount, account?.PasswordHash ?? MissingPasswordHash, password);
            if (account is null || verification == PasswordVerificationResult.Failed
                || !account.IsEnabled || account.RequiresSecondFactor)
            {
                throw new UnauthorizedAccessException("The credentials are unavailable.");
            }

            if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            {
                await context.WritePasswordAsync(
                    new AccountPasswordData(account.Id, PasswordHasher.HashPassword(account, password)), cancellationToken);
            }

            return account;
        }

        public static async Task<long?> CreateAccountAsync(this LogicContext context, AccountCreationData data, long uiLocaleId, CancellationToken cancellationToken)
        {
            // Require an existing Owner.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id"
                FROM
                    "Accounts" AS account
                WHERE
                    account."AccountRoleTypeId" = 1
                    AND account."IsEnabled" = TRUE
                ORDER BY
                    account."Id"
                LIMIT 1
                FOR SHARE OF
                    account;
                """;
            try
            {
                await context.ReadRequiredAsync<AccountIdData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                throw new SecurityException("Owner setup is required before registration.");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Accounts" ("Email", "DisplayName", "AccountRoleTypeId", "IsEnabled", "EmailVerifiedAt")
                VALUES
                    (@Email, @DisplayName, @AccountRoleTypeId, @IsEnabled, @EmailVerifiedAt)
                ON CONFLICT ("Email") DO NOTHING
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);

            var account = await context.ReadOptionalAsync<AccountIdData>(sql, cancellationToken);
            if (account is null)
            {
                // Never replace duplicate credentials.
                return null;
            }

            // Create ownership atomically.
            await context.CreateProfileAsync(new ProfileCreationData(account.Id, data.DisplayName, uiLocaleId), cancellationToken);
            return account.Id;
        }

        public static async Task CreateRegistrationRequestAsync(this LogicContext context, AccountCreationData data, string password, long uiLocaleId, CancellationToken cancellationToken)
        {
            // Hash before account creation.
            var hash = PasswordHasher.HashPassword(MissingAccount, password);
            var accountId = await context.CreateAccountAsync(data, uiLocaleId, cancellationToken);

            if (accountId is { } id)
            {
                await context.WritePasswordAsync(new AccountPasswordData(id, hash), cancellationToken);
            }
        }

        private static async Task WritePasswordAsync(this LogicContext context, AccountPasswordData data, CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountPasswords" ("AccountId", "PasswordHash")
                VALUES
                    (@AccountId, @PasswordHash)
                ON CONFLICT ("AccountId") DO UPDATE
                SET
                    "PasswordHash" = EXCLUDED."PasswordHash",
                    "ChangedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new InvalidOperationException("The password could not be written.");
            }
        }
    }
}
