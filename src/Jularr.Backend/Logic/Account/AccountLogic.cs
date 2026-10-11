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
    public static class AccountLogic
    {
        private static readonly PasswordHasher<AccountAuthenticationData> PasswordHasher = new(
            Options.Create(new PasswordHasherOptions { IterationCount = 600_000 }));
        private static readonly AccountAuthenticationData MissingAccount = new(1, 1, null, false, false);
        private static readonly string MissingPasswordHash = PasswordHasher.HashPassword(MissingAccount, Guid.NewGuid().ToString());

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

            AccountAuthenticationData? account = null;
            try
            {
                account = await context.ReadForUpdateAsync<AccountAuthenticationData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                // Unknown emails still perform password-hash verification.
            }

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

        public static async Task<long?> CreateAccountAsync(
            this LogicContext context,
            AccountCreationData data,
            long uiLocaleId,
            CancellationToken cancellationToken)
        {
            // Public registration never bootstraps an Owner account.
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
                await context.ReadForUpdateAsync<AccountIdData>(sql, cancellationToken);
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

            AccountIdData account;
            try
            {
                account = await context.ExecuteReturningAsync<AccountIdData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                // Duplicate requests must not replace credentials or reveal an existing account.
                return null;
            }

            await context.CreateProfileAsync(new ProfileCreationData(account.Id, data.DisplayName, uiLocaleId), cancellationToken);
            return account.Id;
        }

        public static async Task CreateRegistrationRequestAsync(
            this LogicContext context,
            AccountCreationData data,
            string password,
            long uiLocaleId,
            CancellationToken cancellationToken)
        {
            var hash = PasswordHasher.HashPassword(MissingAccount, password);
            var accountId = await context.CreateAccountAsync(data, uiLocaleId, cancellationToken);

            if (accountId is { } id)
            {
                await context.WritePasswordAsync(new AccountPasswordData(id, hash), cancellationToken);
            }
        }

        private static async Task WritePasswordAsync(
            this LogicContext context,
            AccountPasswordData data,
            CancellationToken cancellationToken)
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
