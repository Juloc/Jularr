using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Account
{
    internal static class AccountProfileLogic
    {
        public static async Task AddAccountProfileAsync(this LogicContext ctx, AccountProfileData data, CancellationToken ct)
        {
            // Link existing canonical entities.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountProfiles" ("AccountId", "ProfileId")
                SELECT
                    account."Id",
                    profile."Id"
                FROM
                    "Accounts" AS account
                CROSS JOIN
                    "Profiles" AS profile
                WHERE
                    account."Id" = @AccountId
                    AND profile."Id" = @ProfileId
                ON CONFLICT ("AccountId", "ProfileId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.ProfileId), "unknown_or_duplicate_id");
            }
        }

        public static async Task RemoveAccountProfileAsync(this LogicContext ctx, AccountProfileData data, CancellationToken ct)
        {
            // Preserve the ownership link.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    accountProfile."AccountId",
                    accountProfile."ProfileId"
                FROM
                    "AccountProfiles" AS accountProfile
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = accountProfile."ProfileId"
                WHERE
                    accountProfile."AccountId" = @AccountId
                    AND accountProfile."ProfileId" = @ProfileId
                    AND profile."OwnerAccountId" <> @AccountId
                FOR UPDATE OF
                    accountProfile, profile;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<AccountProfileData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.ProfileId), "invalid_transition");
            }

            // Detach and revoke affected sessions.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "ActiveProfileId" = NULL,
                    "RevokedAt" = COALESCE(session."RevokedAt", statement_timestamp())
                WHERE
                    session."AccountId" = @AccountId
                    AND session."ActiveProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Remove access without deleting state.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AccountProfiles" AS accountProfile
                WHERE
                    accountProfile."AccountId" = @AccountId
                    AND accountProfile."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The profile assignment changed.");
            }
        }
    }
}
