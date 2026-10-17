using System.Security.Cryptography;
using System.Text;
using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.User.Session.V1;
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
            var token = NewToken();
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
            var token = NewToken();
            var credentials = new SessionCredentialsData(Guid.NewGuid(), account.Id, profileId,
                SHA256.HashData(Encoding.UTF8.GetBytes(token)), token, account.CredentialRevision, DateTime.UtcNow.Add(lifetime));

            await ctx.InsertSessionAsync(credentials, new SessionDeviceData(null, null), ct);
            return credentials;
        }

        // Only the hash is stored; a paired device adds its name and platform.
        public static async Task InsertSessionAsync(this LogicContext ctx, SessionCredentialsData credentials, SessionDeviceData device, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountSessions" ("PublicId", "AccountId", "ActiveProfileId", "TokenHash", "CredentialRevision", "ExpiresAt", "DeviceName", "DevicePlatform")
                VALUES
                    (@PublicId, @AccountId, @ProfileId, @TokenHash, @CredentialRevision, @ExpiresAt, @DeviceName, @DevicePlatform);
                """;
            sql.Parameters.Add(credentials);
            sql.Parameters.Add(device);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The session could not be created.");
            }
        }

        public static async Task<SessionAuthenticationData> ReadSessionAsync(this LogicContext ctx, SessionParameters parameters, CancellationToken ct)
        {
            // Account, session, then profile; foreign-key readers may proceed.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH lockedAccount AS MATERIALIZED (
                    SELECT
                        account."Id",
                        account."AccountRoleTypeId",
                        account."CredentialRevision"
                    FROM
                        "Accounts" AS account
                    WHERE
                        account."IsEnabled" = TRUE
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "AccountSessions" AS session
                            WHERE
                                session."AccountId" = account."Id"
                                AND session."PublicId" = @SessionPublicId
                                AND session."RotationRevision" = @SessionRevision
                                AND session."TokenHash" = @SessionTokenHash
                                AND session."RevokedAt" IS NULL
                                AND session."ExpiresAt" > statement_timestamp()
                                AND session."CredentialRevision" = account."CredentialRevision"
                        )
                    FOR NO KEY UPDATE OF
                        account
                ), lockedSession AS MATERIALIZED (
                    SELECT
                        session."AccountId",
                        session."ActiveProfileId",
                        session."DevicePlatform",
                        session."ExpiresAt",
                        account."AccountRoleTypeId"
                    FROM
                        lockedAccount AS account
                    INNER JOIN
                        "AccountSessions" AS session ON session."AccountId" = account."Id"
                    WHERE
                        session."PublicId" = @SessionPublicId
                        AND session."RotationRevision" = @SessionRevision
                        AND session."TokenHash" = @SessionTokenHash
                        AND session."RevokedAt" IS NULL
                        AND session."ExpiresAt" > statement_timestamp()
                        AND session."CredentialRevision" = account."CredentialRevision"
                    FOR NO KEY UPDATE OF
                        session
                )
                SELECT
                    session."AccountId",
                    session."AccountRoleTypeId",
                    profile."Id" AS "ProfileId",
                    profile."OwnerAccountId" AS "ProfileOwnerAccountId",
                    profile."UiLocaleId",
                    session."DevicePlatform" IS NOT NULL AS "IsDevice",
                    session."ExpiresAt"
                FROM
                    lockedSession AS session
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
                        accountProfile."AccountId" = session."AccountId"
                        AND accountProfile."ProfileId" = session."ActiveProfileId"
                    FOR NO KEY UPDATE OF
                        accountProfile, profile
                ) AS profile ON TRUE
                WHERE
                    session."ActiveProfileId" IS NULL OR profile."Id" IS NOT NULL;
                """;
            sql.Parameters.Add(parameters);

            return Effective(await ctx.ReadOptionalAsync<SessionAuthenticationData>(sql, ct)
                ?? throw new UnauthorizedAccessException("The profile session is unavailable."));
        }

        // A paired device is a normal user of its profile, whatever its account may do elsewhere.
        public static SessionAuthenticationData Effective(SessionAuthenticationData session) => session.IsDevice
            ? new SessionAuthenticationData(session.AccountId, session.ProfileId, session.ProfileOwnerAccountId, session.UiLocaleId, AccountRoleType.User, true, session.ExpiresAt)
            : session;

        // Only a paired device of the account has a name to change.
        public static async Task RenameSessionDeviceAsync(this LogicContext ctx, SessionRevocationData session, SessionDeviceData device, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "DeviceName" = @DeviceName
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."AccountId" = @AccountId
                    AND session."DevicePlatform" IS NOT NULL
                    AND session."RevokedAt" IS NULL;
                """;
            sql.Parameters.Add(session);
            sql.Parameters.Add(device);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The device was not found.");
            }
        }

        public static async Task SetSessionAudioQualityAsync(this LogicContext ctx, SessionRevocationData session, SessionAudioQualityDataV1 quality, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountSessions" AS session
                SET
                    "AudioQuality" = @AudioQuality
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."AccountId" = @AccountId
                    AND session."RevokedAt" IS NULL;
                """;
            sql.Parameters.Add(session);
            sql.Parameters.Add(quality);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The session was not found.");
            }
        }

        // A session secret: 32 random bytes, URL-safe.
        public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
