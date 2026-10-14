using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Session
{
    // A TV, phone or app is paired like a sign-in on another device: it shows a code, a signed-in profile confirms it,
    // and the device fetches its own session once.
    internal static class DevicePairingLogic
    {
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan DeviceSessionLifetime = TimeSpan.FromDays(365);

        public static async Task<DevicePairingStartedData> StartDevicePairingAsync(this LogicContext ctx, string deviceName, string devicePlatform, CancellationToken ct)
        {
            // Codes of expired pairings become free again.
            var cleanup = new SqlStatement();
            cleanup.Statement =
                """
                DELETE FROM
                    "DevicePairings" AS pairing
                WHERE
                    pairing."ExpiresAt" < statement_timestamp();
                """;
            await ctx.ExecuteAsyncForAffectedRows(cleanup, ct);

            // A code in use is drawn again.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    INSERT INTO
                        "DevicePairings" ("Code", "DeviceName", "DevicePlatform", "ExpiresAt")
                    VALUES
                        (@Code, @DeviceName, @DevicePlatform, @ExpiresAt)
                    ON CONFLICT ("Code") DO NOTHING
                    RETURNING
                        "PublicId",
                        "Code",
                        "ExpiresAt";
                    """;
                sql.Parameters.Add(new DevicePairingStartData(code, deviceName, devicePlatform, DateTime.UtcNow.Add(CodeLifetime)));
                if (await ctx.ReadOptionalAsync<DevicePairingStartedData>(sql, ct) is { } started)
                {
                    return started;
                }
            }

            throw new ServiceUnavailableException("device_pairing_busy");
        }

        public static async Task ApproveDevicePairingAsync(this LogicContext ctx, DevicePairingApprovalData data, CancellationToken ct)
        {
            // Only an open, unexpired code is taken, once.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "DevicePairings" AS pairing
                SET
                    "ApprovedAccountId" = @ApprovedAccountId,
                    "ApprovedProfileId" = @ApprovedProfileId
                WHERE
                    pairing."Code" = @Code
                    AND pairing."ApprovedAccountId" IS NULL
                    AND pairing."ExpiresAt" > statement_timestamp();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Code), "invalid_code");
            }
        }

        // Null while the code waits for its confirmation; an unknown or expired pairing is not found.
        public static async Task<SessionCredentialsData?> ClaimDevicePairingAsync(this LogicContext ctx, DevicePairingParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pairing."Id",
                    pairing."DeviceName",
                    pairing."DevicePlatform",
                    pairing."ApprovedAccountId",
                    pairing."ApprovedProfileId"
                FROM
                    "DevicePairings" AS pairing
                WHERE
                    pairing."PublicId" = @PublicId
                    AND pairing."ExpiresAt" > statement_timestamp()
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var pairing = await ctx.ReadOptionalAsync<DevicePairingStateData>(sql, ct) ?? throw new KeyNotFoundException("The pairing was not found.");
            if (pairing is not { ApprovedAccountId: { } accountId, ApprovedProfileId: { } profileId })
            {
                return null;
            }

            // The session carries the account's current credentials, so a password change ends it like any other.
            var revision = new SqlStatement();
            revision.Statement =
                """
                SELECT
                    account."CredentialRevision"
                FROM
                    "Accounts" AS account
                WHERE
                    account."Id" = @AccountId
                    AND account."IsEnabled";
                """;
            revision.Parameters.Add(new Data.Logic.Account.AccountParameters(accountId));
            var credentials = await ctx.ReadOptionalAsync<DeviceSessionRevisionData>(revision, ct) ?? throw new KeyNotFoundException("The account is not available.");

            var token = SessionLogic.NewToken();
            var session = new DeviceSessionData(Guid.NewGuid(), accountId, profileId, SHA256.HashData(Encoding.UTF8.GetBytes(token)), DateTime.UtcNow.Add(DeviceSessionLifetime),
                pairing.DeviceName, pairing.DevicePlatform);
            var insert = new SqlStatement();
            insert.Statement =
                """
                INSERT INTO
                    "AccountSessions" ("PublicId", "AccountId", "ActiveProfileId", "TokenHash", "CredentialRevision", "ExpiresAt", "DeviceName", "DevicePlatform")
                VALUES
                    (@PublicId, @AccountId, @ProfileId, @TokenHash, @CredentialRevision, @ExpiresAt, @DeviceName, @DevicePlatform);

                DELETE FROM
                    "DevicePairings" AS pairing
                WHERE
                    pairing."Id" = @Id;
                """;
            insert.Parameters.Add(session);
            insert.Parameters.Add(credentials);
            insert.Parameters.Add(new DevicePairingIdData(pairing.Id));
            await ctx.ExecuteAsyncForAffectedRows(insert, ct);
            return new SessionCredentialsData(session.PublicId, accountId, profileId, session.TokenHash, token, credentials.CredentialRevision, session.ExpiresAt);
        }
    }
}
