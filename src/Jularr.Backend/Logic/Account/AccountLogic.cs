using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Logic.Account;
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

        public static async Task<long?> CreateAccountAsync(this LogicContext ctx, AccountCreationData data, long uiLocaleId, CancellationToken ct)
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
                    "Accounts" ("Username", "Email", "DisplayName", "AccountRoleTypeId", "IsEnabled", "EmailVerifiedAt")
                VALUES
                    (@Username, @Email, @DisplayName, @AccountRoleTypeId, @IsEnabled, @EmailVerifiedAt)
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
            await ctx.CreateProfileAsync(new ProfileCreationData(account.Id, data.DisplayName, uiLocaleId), ct);
            return account.Id;
        }

        public static async Task<AccountAuthenticationData> CreateFirstOwnerAsync(this LogicContext ctx, string username, string displayName, string password, long uiLocaleId, CancellationToken ct)
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
            var data = new AccountCreationData(username, null, displayName, true, null, AccountRoleType.Owner);
            var id = await ctx.CreateAccountAsync(data, uiLocaleId, ct)
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
