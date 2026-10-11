using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Session
{
    public static class SessionLogic
    {
        public static async Task<SessionCredentialsData> CreateSessionAsync(this LogicContext context, AccountAuthenticationData account, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."DisplayName",
                    profile."UiLocaleId"
                FROM
                    "AccountProfiles" AS membership
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = membership."ProfileId"
                WHERE
                    membership."AccountId" = @AccountId
                    AND profile."PinHash" IS NULL
                ORDER BY
                    (profile."OwnerAccountId" = @AccountId) DESC,
                    profile."Id"
                LIMIT 1
                FOR UPDATE OF
                    membership, profile;
                """;
            sql.Parameters.Add(new SessionAccountParameters(account.Id));

            // A profile remains optional.
            var profile = await context.ReadOptionalAsync<ProfileUpdateData>(sql, cancellationToken);
            var profileId = profile?.Id;

            // Issue a separate secret.
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var credentials = new SessionCredentialsData(Guid.NewGuid(), account.Id, profileId,
                SHA256.HashData(Encoding.UTF8.GetBytes(token)), token, account.CredentialRevision, DateTime.UtcNow.Add(lifetime));

            // Store the session hash.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountSessions" ("PublicId", "AccountId", "ActiveProfileId", "TokenHash", "CredentialRevision", "ExpiresAt")
                VALUES
                    (@PublicId, @AccountId, @ProfileId, @TokenHash, @CredentialRevision, @ExpiresAt);
                """;
            sql.Parameters.Add(credentials);
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new InvalidOperationException("The session could not be created.");
            }

            return credentials;
        }

        public static async Task<SessionAuthenticationData> ReadSessionAsync(this LogicContext context, SessionParameters parameters, CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id" AS "AccountId",
                    profile."Id" AS "ProfileId",
                    profile."OwnerAccountId" AS "ProfileOwnerAccountId"
                FROM
                    "AccountSessions" AS session
                INNER JOIN
                    "Accounts" AS account ON account."Id" = session."AccountId"
                INNER JOIN
                    "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
                        AND membership."ProfileId" = session."ActiveProfileId"
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = membership."ProfileId"
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."RotationRevision" = @SessionRevision
                    AND session."TokenHash" = @SessionTokenHash
                    AND session."RevokedAt" IS NULL
                    AND session."ExpiresAt" > statement_timestamp()
                    AND session."CredentialRevision" = account."CredentialRevision"
                    AND account."IsEnabled" = TRUE
                FOR UPDATE OF
                    account, membership, profile, session;
                """;
            sql.Parameters.Add(parameters);

            try
            {
                return await context.ReadRequiredAsync<SessionAuthenticationData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                throw new UnauthorizedAccessException("The profile session is unavailable.");
            }
        }
    }
}
