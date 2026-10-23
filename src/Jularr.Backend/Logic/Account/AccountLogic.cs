using System.Security;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Authentication;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.Logic.Setup;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Logic.Localization;
using Jularr.Backend.Logic.Profile;
using Jularr.Backend.Logic.Session;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Logic.Account
{
    internal static class AccountLogic
    {
        private static readonly PasswordHasher<AccountAuthenticationData> PasswordHasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 600_000 }));
        private static readonly AccountAuthenticationData MissingAccount = new(1, 1, null, false, false);
        private static readonly string MissingPasswordHash = PasswordHasher.HashPassword(MissingAccount, Guid.NewGuid().ToString());

        public static async Task UpdateAccountAsync(this LogicContext ctx, AccountUpdateData data, CancellationToken ct)
        {
            // Retain an enabled Owner.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Accounts" AS account
                SET
                    "DisplayName" = @DisplayName,
                    "AccountRoleTypeId" = @AccountRoleTypeId,
                    "IsEnabled" = @IsEnabled,
                    "MayConfirmSources" = coalesce(@MayConfirmSources, account."MayConfirmSources"),
                    "TranscodingPriority" = coalesce(@TranscodingPriority, account."TranscodingPriority"),
                    "CredentialRevision" = account."CredentialRevision" + CASE
                        WHEN account."AccountRoleTypeId" <> @AccountRoleTypeId OR account."IsEnabled" <> @IsEnabled THEN 1
                        ELSE 0
                    END,
                    "UpdatedAt" = clock_timestamp()
                WHERE
                    account."Id" = @Id
                    AND (
                        account."AccountRoleTypeId" <> 1
                        OR (@AccountRoleTypeId = 1 AND @IsEnabled)
                        OR EXISTS (
                            SELECT
                                1
                            FROM
                                "Accounts" AS owner
                            WHERE
                                owner."Id" <> @Id
                                AND owner."AccountRoleTypeId" = 1
                                AND owner."IsEnabled"
                        )
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.AccountRoleTypeId), "invalid_transition");
            }

            // Same transaction, canonical Session writer.
            await ctx.RevokeOutdatedAccountSessionsAsync(new AccountParameters(data.Id), ct);
        }

        public static async Task<AccountAuthenticationData> AuthenticatePasswordAsync(this LogicContext ctx, AccountLoginParameters parameters, string password, CancellationToken ct)
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
                    account."Username" = @Login
                    OR account."Email" = @Login
                FOR UPDATE OF
                    account, password;
                """;
            sql.Parameters.Add(parameters);

            var account = await ctx.ReadOptionalAsync<AccountAuthenticationData>(sql, ct);

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
                await ctx.WritePasswordAsync(
                    new AccountPasswordData(account.Id, PasswordHasher.HashPassword(account, password)), ct);
            }

            return account;
        }

        public static async Task<long?> CreateAccountAsync(this LogicContext ctx, AccountCreationData data, long uiLocaleId, CancellationToken ct, string? profileName = null)
        {
            // Registration follows Owner setup.
            if (data.AccountRoleTypeId != AccountRoleType.Owner)
            {
                var ownerSql = new SqlStatement();
                ownerSql.Statement =
                    """
                    SELECT
                        account."Id"
                    FROM
                        "Accounts" AS account
                    WHERE
                        account."AccountRoleTypeId" = 1
                        AND account."IsEnabled"
                    ORDER BY
                        account."Id"
                    LIMIT 1
                    FOR SHARE OF
                        account;
                    """;
                if (await ctx.ReadOptionalAsync<AccountIdData>(ownerSql, ct) is null)
                {
                    throw new SecurityException("Owner setup is required before registration.");
                }
            }

            // Create the account once.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "Accounts" ("Username", "Email", "DisplayName", "AccountRoleTypeId", "IsEnabled", "EmailVerifiedAt", "TranscodingPriority")
                VALUES
                    (@Username, @Email, @DisplayName, @AccountRoleTypeId, @IsEnabled, @EmailVerifiedAt, @AccountRoleTypeId::smallint = 1)
                ON CONFLICT DO NOTHING
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            var account = await ctx.ReadOptionalAsync<AccountIdData>(sql, ct);
            if (account is null)
            {
                return null;
            }

            // Create ownership atomically.
            await ctx.CreateProfileAsync(new ProfileCreationData(account.Id, profileName ?? data.DisplayName, uiLocaleId), ct);
            return account.Id;
        }

        public static async Task<AccountAuthenticationData> CreateFirstOwnerAsync(this LogicContext ctx, string username, string profileName, string password, long uiLocaleId, CancellationToken ct)
        {
            AccountPasswordPolicy.Require(password, "Password");

            // Serialize first-run account creation.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    setup."Id",
                    setup."OwnerCreatedAt"
                FROM
                    "InstanceSetupState" AS setup
                WHERE
                    setup."Id" = 1
                FOR UPDATE OF
                    setup;
                """;
            var setup = await ctx.ReadRequiredAsync<InstanceSetupStateData>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM "Accounts"
                );
                """;
            if (setup.OwnerCreatedAt is not null || (bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!)
            {
                throw new SecurityException("Initial Owner creation is closed.");
            }

            // Reuse canonical account/profile writes.
            await ctx.EnableInitialUiLocaleAsync(uiLocaleId, ct);
            await ctx.SetInitialInstanceLocaleAsync(uiLocaleId, ct);
            var data = new AccountCreationData(username, null, username, true, null, AccountRoleType.Owner);
            var id = await ctx.CreateAccountAsync(data, uiLocaleId, ct, profileName)
                ?? throw new SecurityException("Initial Owner creation is unavailable.");
            var account = new AccountAuthenticationData(id, 1, null, true, false);
            await ctx.WritePasswordAsync(new AccountPasswordData(id, PasswordHasher.HashPassword(account, password)), ct);

            // Close bootstrap permanently on commit.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSetupState"
                SET
                    "OwnerCreatedAt" = statement_timestamp()
                WHERE
                    "Id" = 1
                    AND "OwnerCreatedAt" IS NULL;
                """;
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new SecurityException("Initial Owner creation is closed.");
            }

            return account;
        }

        public static async Task CreateRegistrationRequestAsync(this LogicContext ctx, AccountCreationData data, string password, long uiLocaleId, CancellationToken ct)
        {
            AccountPasswordPolicy.Require(password, "Password");

            // Hash before account creation.
            var hash = PasswordHasher.HashPassword(MissingAccount, password);
            var accountId = await ctx.CreateAccountAsync(data, uiLocaleId, ct)
                ?? throw new DataValidationException("Username", "username_taken");

            await ctx.WritePasswordAsync(new AccountPasswordData(accountId, hash), ct);
        }

        // A free username from a name that may hold other characters (a Plex name): usable characters only, 3 to 32 of them, a number added when taken.
        public static async Task<string> UniqueUsernameAsync(this LogicContext ctx, string seed, CancellationToken ct)
        {
            var cleaned = new string([.. seed.Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_')]).Trim('_');
            if (cleaned.Length < 3) cleaned = (cleaned + "user").PadRight(3, '_');
            cleaned = cleaned[..Math.Min(cleaned.Length, 28)];
            for (var number = 1; ; number++)
            {
                var candidate = number == 1 ? cleaned : cleaned + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var sql = new SqlStatement();
                sql.Statement = """SELECT EXISTS (SELECT 1 FROM "Accounts" WHERE "Username" = @Username);""";
                sql.Parameters.Add(new AccountUsernameParameters(candidate));
                if (!(bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!)
                {
                    return candidate;
                }
            }
        }

        // The signed-in account changes its own password: the current one is verified in the same transaction.
        public static async Task ChangePasswordAsync(this LogicContext ctx, long accountId, string currentPassword, string newPassword, CancellationToken ct)
        {
            AccountPasswordPolicy.Require(newPassword, "NewPassword");
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id",
                    account."CredentialRevision",
                    password."PasswordHash",
                    account."IsEnabled",
                    FALSE AS "RequiresSecondFactor"
                FROM
                    "Accounts" AS account
                INNER JOIN
                    "AccountPasswords" AS password ON password."AccountId" = account."Id"
                WHERE
                    account."Id" = @AccountId
                FOR UPDATE OF
                    account, password;
                """;
            sql.Parameters.Add(new AccountParameters(accountId));
            var account = await ctx.ReadOptionalAsync<AccountAuthenticationData>(sql, ct);
            if (account?.PasswordHash is null
                || PasswordHasher.VerifyHashedPassword(account, account.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            {
                throw new DataValidationException("CurrentPassword", "password_incorrect");
            }

            await ctx.WritePasswordAsync(new AccountPasswordData(accountId, PasswordHasher.HashPassword(account, newPassword)), ct);
        }

        // An administrator creates a link for an account; only the hash of its token is stored and an older open link ends.
        public static async Task<(string Token, DateTime ExpiresAt)> CreatePasswordResetAsync(this LogicContext ctx, long accountId, CancellationToken ct)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var expiresAt = DateTime.UtcNow.AddHours(24);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountAuthChallenges"
                SET
                    "ConsumedAt" = statement_timestamp()
                WHERE
                    "AccountId" = @AccountId
                    AND "AuthChallengePurposeTypeId" = 2
                    AND "ConsumedAt" IS NULL;
                """;
            sql.Parameters.Add(new AccountParameters(accountId));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountAuthChallenges" ("AccountId", "AuthChallengePurposeTypeId", "TokenHash", "CredentialRevision", "ExpiresAt")
                SELECT
                    account."Id", @AuthChallengePurposeTypeId, @TokenHash, account."CredentialRevision", @ExpiresAt
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @AccountId;
                """;
            sql.Parameters.Add(new PasswordResetCreationData(accountId, SHA256.HashData(Encoding.UTF8.GetBytes(token)), expiresAt));
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The account was not found.");
            }

            return (token, expiresAt);
        }

        // The link works once, for the revision of the credentials it was made for; a new password ends every open session.
        public static async Task ResetPasswordAsync(this LogicContext ctx, string token, string newPassword, CancellationToken ct)
        {
            AccountPasswordPolicy.Require(newPassword, "NewPassword");
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    challenge."Id",
                    challenge."AccountId"
                FROM
                    "AccountAuthChallenges" AS challenge
                INNER JOIN
                    "Accounts" AS account ON account."Id" = challenge."AccountId"
                WHERE
                    challenge."TokenHash" = @TokenHash
                    AND challenge."AuthChallengePurposeTypeId" = 2
                    AND challenge."ConsumedAt" IS NULL
                    AND challenge."ExpiresAt" > statement_timestamp()
                    AND challenge."CredentialRevision" = account."CredentialRevision"
                    AND account."IsEnabled"
                FOR UPDATE OF
                    challenge, account;
                """;
            sql.Parameters.Add(new PasswordResetTokenData(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
            var claim = await ctx.ReadOptionalAsync<PasswordResetClaimData>(sql, ct)
                ?? throw new DataValidationException("Token", "reset_invalid");

            var account = new AccountAuthenticationData(claim.AccountId, 1, null, true, false);
            await ctx.WritePasswordAsync(new AccountPasswordData(claim.AccountId, PasswordHasher.HashPassword(account, newPassword)), ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountAuthChallenges"
                SET
                    "ConsumedAt" = statement_timestamp()
                WHERE
                    "Id" = @Id;
                """;
            sql.Parameters.Add(new AuthChallengeIdData(claim.Id));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "Accounts"
                SET
                    "CredentialRevision" = "CredentialRevision" + 1
                WHERE
                    "Id" = @AccountId;
                """;
            sql.Parameters.Add(new AccountParameters(claim.AccountId));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            await ctx.RevokeOutdatedAccountSessionsAsync(new AccountParameters(claim.AccountId), ct);
        }

        private static async Task WritePasswordAsync(this LogicContext ctx, AccountPasswordData data, CancellationToken ct)
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
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The password could not be written.");
            }
        }
    }
}
