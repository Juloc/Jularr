using System.Security.Cryptography;
using System.Text;
using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;

namespace Jularr.Backend.Logic.Session
{
    internal static class SessionLogic
    {
        public static async Task RenewSessionAsync(this LogicContext ctx, SessionParameters proof, SessionRenewalData data, CancellationToken ct)
        {
            // Retain the current proof.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "ExpiresAt" = GREATEST(session."ExpiresAt", @ExpiresAt),
                    "LastSeenAt" = statement_timestamp()
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."RotationRevision" = @SessionRevision
                    AND session."TokenHash" = @SessionTokenHash;
                """;
            sql.Parameters.Add(proof);
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new UnauthorizedAccessException("The account session changed.");
            }
        }

        public static async Task RevokeOutdatedAccountSessionsAsync(this LogicContext ctx, AccountParameters parameters, CancellationToken ct)
        {
            // Revoke obsolete credential proofs.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "RevokedAt" = statement_timestamp()
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @AccountId
                    AND session."AccountId" = account."Id"
                    AND session."CredentialRevision" <> account."CredentialRevision"
                    AND session."RevokedAt" IS NULL;
                """;
            sql.Parameters.Add(parameters);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task<SessionCredentialsData?> SelectSessionProfileAsync(this LogicContext ctx, SessionParameters proof, AccountProfileData target, string? pin, CancellationToken ct)
        {
            // Lock the account/profile link.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId",
                    CASE WHEN selectable."IsPinRequired" THEN profile."PinHash" END AS "PinHash",
                    COALESCE(profile."PinLockedUntil" > statement_timestamp(), FALSE) AS "IsPinLocked",
                    session."ExpiresAt",
                    session."CredentialRevision"
                FROM
                    "AccountProfiles" AS accountProfile
                INNER JOIN
                    "Profiles" AS profile ON profile."Id" = accountProfile."ProfileId"
                INNER JOIN
                    "SelectableProfiles" AS selectable ON selectable."AccountId" = accountProfile."AccountId" AND selectable."ProfileId" = accountProfile."ProfileId"
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
            var profile = await ctx.ReadRequiredAsync<SessionProfileData>(sql, ct);
            if (profile.PinHash is not null)
            {
                // PIN access remains fail-closed; a locked profile verifies nothing.
                if (pin is null)
                {
                    throw new SecurityException("The profile requires PIN verification.");
                }

                if (profile.IsPinLocked)
                {
                    throw new DataValidationException("Pin", "pin_locked");
                }

                var isValid = ProfilePinLogic.VerifyPin(profile.PinHash, pin);
                await ctx.RecordPinAttemptAsync(profile.ProfileId, isValid, ct);
                if (!isValid)
                {
                    return null;
                }
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
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new UnauthorizedAccessException("The account session changed.");
            }

            return credentials;
        }

        public static async Task RevokeSessionAsync(this LogicContext ctx, SessionRevocationData data, CancellationToken ct)
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
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The account session was not found.");
            }
        }

        public static async Task<SessionCredentialsData> CreateSessionAsync(this LogicContext ctx, AccountAuthenticationData account, TimeSpan lifetime, CancellationToken ct)
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
                INNER JOIN
                    "SelectableProfiles" AS selectable ON selectable."AccountId" = accountProfile."AccountId" AND selectable."ProfileId" = accountProfile."ProfileId"
                WHERE
                    accountProfile."AccountId" = @AccountId
                    AND NOT selectable."IsPinRequired"
                    AND (SELECT count(*) FROM "SelectableProfiles" AS other WHERE other."AccountId" = @AccountId) = 1
                ORDER BY
                    (profile."OwnerAccountId" = @AccountId) DESC,
                    profile."Id"
                LIMIT 1
                FOR UPDATE OF
                    accountProfile, profile;
                """;
            sql.Parameters.Add(new AccountParameters(account.Id));

            // A profile remains optional.
            var profile = await ctx.ReadOptionalAsync<ProfileUpdateData>(sql, ct);
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
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The session could not be created.");
            }

            return credentials;
        }

        public static async Task<SessionAuthenticationData> ReadSessionAsync(this LogicContext ctx, SessionParameters parameters, CancellationToken ct)
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

            return await ctx.ReadOptionalAsync<SessionAuthenticationData>(sql, ct)
                ?? throw new UnauthorizedAccessException("The profile session is unavailable.");
        }
    }
}
