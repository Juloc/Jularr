using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Session
{
    // A TV, phone or app is paired like a sign-in on another device: it shows a code for ten minutes, a signed-in profile confirms it,
    // and the device fetches its own session once; it lasts and renews like any session.
    internal static class DevicePairingLogic
    {
        public static async Task<DevicePairingStartedData> StartDevicePairingAsync(this LogicContext ctx, string? deviceName, string devicePlatform, Guid? replacesPairingId,
            CancellationToken ct)
        {
            // Codes of expired pairings become free again; a device asking anew ends its own open code at once.
            var cleanup = new SqlStatement();
            cleanup.Statement =
                """
                DELETE FROM
                    "DevicePairings" AS pairing
                WHERE
                    pairing."ExpiresAt" < statement_timestamp()
                    OR pairing."PublicId" = @ReplacesPairingId AND pairing."ApprovedAccountId" IS NULL;
                """;
            cleanup.Parameters.Add(new DevicePairingReplacementData(replacesPairingId));
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
                        (@Code, @DeviceName, @DevicePlatform, statement_timestamp() + interval '10 minutes')
                    ON CONFLICT ("Code") DO NOTHING
                    RETURNING
                        "PublicId",
                        "Code",
                        "ExpiresAt";
                    """;
                sql.Parameters.Add(new DevicePairingStartData(code, deviceName, devicePlatform));
                if (await ctx.ReadOptionalAsync<DevicePairingStartedData>(sql, ct) is { } started)
                {
                    return started;
                }
            }

            throw new ServiceUnavailableException("device_pairing_busy");
        }

        // An attempt is recorded before a code is checked and removed when it was right; five wrong ones in ten minutes and the account waits.
        public static async Task<DevicePairingAttemptData> TakeDevicePairingAttemptAsync(this LogicContext ctx, DevicePairingAccountData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pg_advisory_xact_lock(hashtextextended('device-pairing-attempt', @AccountId));

                DELETE FROM
                    "DevicePairingAttempts" AS attempt
                WHERE
                    attempt."AccountId" = @AccountId
                    AND attempt."AttemptedAt" < statement_timestamp() - interval '10 minutes';
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            var take = new SqlStatement();
            take.Statement =
                """
                INSERT INTO
                    "DevicePairingAttempts" ("AccountId")
                SELECT
                    @AccountId
                WHERE
                    (
                        SELECT
                            count(*)
                        FROM
                            "DevicePairingAttempts" AS attempt
                        WHERE
                            attempt."AccountId" = @AccountId
                    ) < 5
                RETURNING
                    "Id";
                """;
            take.Parameters.Add(data);
            return await ctx.ReadOptionalAsync<DevicePairingAttemptData>(take, ct) ?? throw new ServiceUnavailableException("device_pairing_attempts");
        }

        public static async Task ReturnDevicePairingAttemptAsync(this LogicContext ctx, DevicePairingAttemptData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "DevicePairingAttempts" AS attempt
                WHERE
                    attempt."Id" = @Id;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<DevicePairingDeviceData?> ReadOpenDevicePairingAsync(this LogicContext ctx, DevicePairingCodeData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pairing."Id",
                    pairing."DeviceName",
                    pairing."DevicePlatform"
                FROM
                    "DevicePairings" AS pairing
                WHERE
                    pairing."Code" = @Code
                    AND pairing."ApprovedAccountId" IS NULL
                    AND pairing."ExpiresAt" > statement_timestamp();
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadOptionalAsync<DevicePairingDeviceData>(sql, ct);
        }

        public static async Task ApproveDevicePairingAsync(this LogicContext ctx, DevicePairingApprovalData data, CancellationToken ct)
        {
            // Only the inspected pairing, open and unexpired, is taken, once; a code shown again elsewhere is another pairing.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "DevicePairings" AS pairing
                SET
                    "ApprovedAccountId" = @ApprovedAccountId,
                    "ApprovedProfileId" = @ApprovedProfileId
                WHERE
                    pairing."Id" = @Id
                    AND pairing."Code" = @Code
                    AND pairing."ApprovedAccountId" IS NULL
                    AND pairing."ExpiresAt" > statement_timestamp();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Code), "pairing_expired");
            }
        }

        // Null while the code waits for its confirmation; an unknown or expired pairing is not found.
        public static async Task<SessionCredentialsData?> ClaimDevicePairingAsync(this LogicContext ctx, DevicePairingParameters data, TimeSpan lifetime, CancellationToken ct)
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
                    AND account."IsEnabled"
                FOR SHARE;
                """;
            revision.Parameters.Add(new Data.Logic.Account.AccountParameters(accountId));
            var credentials = await ctx.ReadOptionalAsync<DeviceSessionRevisionData>(revision, ct) ?? throw new KeyNotFoundException("The account is not available.");

            var token = SessionLogic.NewToken();
            var session = new SessionCredentialsData(Guid.NewGuid(), accountId, profileId, SHA256.HashData(Encoding.UTF8.GetBytes(token)), token, credentials.CredentialRevision,
                DateTime.UtcNow.Add(lifetime));
            await ctx.InsertSessionAsync(session, new SessionDeviceData(pairing.DeviceName, pairing.DevicePlatform), ct);

            var claimed = new SqlStatement();
            claimed.Statement =
                """
                DELETE FROM
                    "DevicePairings" AS pairing
                WHERE
                    pairing."Id" = @Id;
                """;
            claimed.Parameters.Add(new DevicePairingIdData(pairing.Id));
            if (await ctx.ExecuteAsyncForAffectedRows(claimed, ct) != 1)
            {
                throw new InvalidOperationException("The pairing could not be closed.");
            }

            return session;
        }
    }
}
