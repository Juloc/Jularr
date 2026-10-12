using System.Security.Cryptography;
using System.Text;
using System.Security;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Session
{
    internal static class SessionLogic
    {
        public static async Task<SessionCredentialsData> SelectSessionProfileAsync(this LogicContext context, SessionParameters proof, AccountProfileData target, CancellationToken cancellationToken)
        {
            // Lock the account/profile link.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId",
                    profile."PinHash",
                    session."ExpiresAt",
                    session."CredentialRevision"
                FROM
                    "AccountProfiles" AS accountProfile
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = accountProfile."ProfileId"
                INNER JOIN
                    "AccountSessions" AS session ON session."AccountId" = accountProfile."AccountId"
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND accountProfile."AccountId" = @AccountId
                    AND accountProfile."ProfileId" = @ProfileId
                FOR UPDATE OF
                    accountProfile, profile;
                """;
            sql.Parameters.Add(proof);
            sql.Parameters.Add(target);
            var profile = await context.ReadRequiredAsync<SessionProfileData>(sql, cancellationToken);
            if (profile.PinHash is not null)
            {
                // PIN access remains fail-closed.
                throw new SecurityException("The profile requires PIN verification.");
            }

            // Replace both identifier and secret.
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var credentials = new SessionCredentialsData(Guid.NewGuid(), target.AccountId, profile.ProfileId, SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                token, profile.CredentialRevision, profile.ExpiresAt, checked(proof.SessionRevision + 1));

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "PublicId" = @PublicId,
                    "ActiveProfileId" = @ProfileId,
                    "TokenHash" = @TokenHash,
                    "RotationRevision" = @RotationRevision,
                    "RotatedAt" = statement_timestamp()
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."RotationRevision" = @SessionRevision;
                """;
            sql.Parameters.Add(proof);
            sql.Parameters.Add(credentials);
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new UnauthorizedAccessException("The account session changed.");
            }

            return credentials;
        }

        public static async Task RevokeSessionAsync(this LogicContext context, SessionRevocationData data, CancellationToken cancellationToken)
        {
            // Scope the canonical mutation.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "RevokedAt" = COALESCE(session."RevokedAt", statement_timestamp())
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."AccountId" = @AccountId;
                """;
            sql.Parameters.Add(data);
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new KeyNotFoundException("The account session was not found.");
            }
        }

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
                    "AccountProfiles" AS accountProfile
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = accountProfile."ProfileId"
                WHERE
                    accountProfile."AccountId" = @AccountId
                    AND profile."PinHash" IS NULL
                    AND (SELECT count(*) FROM "AccountProfiles" WHERE "AccountId" = @AccountId) = 1
                ORDER BY
                    (profile."OwnerAccountId" = @AccountId) DESC,
                    profile."Id"
                LIMIT 1
                FOR UPDATE OF
                    accountProfile, profile;
                """;
            sql.Parameters.Add(new AccountParameters(account.Id));

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
                    account."AccountRoleTypeId",
                    profile."Id" AS "ProfileId",
                    profile."OwnerAccountId" AS "ProfileOwnerAccountId",
                    profile."UiLocaleId"
                FROM
                    "AccountSessions" AS session
                INNER JOIN
                    "Accounts" AS account ON account."Id" = session."AccountId"
                LEFT JOIN LATERAL (
                    SELECT
                        profile."Id",
                        profile."OwnerAccountId",
                        profile."UiLocaleId"
                    FROM
                        "AccountProfiles" AS accountProfile
                    INNER JOIN
                        "Profiles" AS profile ON profile."Id" = accountProfile."ProfileId"
                    WHERE
                        accountProfile."AccountId" = account."Id"
                        AND accountProfile."ProfileId" = session."ActiveProfileId"
                    FOR UPDATE OF
                        accountProfile, profile
                ) AS profile ON TRUE
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."RotationRevision" = @SessionRevision
                    AND session."TokenHash" = @SessionTokenHash
                    AND session."RevokedAt" IS NULL
                    AND session."ExpiresAt" > statement_timestamp()
                    AND session."CredentialRevision" = account."CredentialRevision"
                    AND account."IsEnabled" = TRUE
                    AND (session."ActiveProfileId" IS NULL OR profile."Id" IS NOT NULL)
                FOR UPDATE OF
                    account, session;
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
