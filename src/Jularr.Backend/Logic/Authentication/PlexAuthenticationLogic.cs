using System.Security;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Authentication;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Session;

namespace Jularr.Backend.Logic.Authentication
{
    public static class PlexAuthenticationLogic
    {
        public static async Task CreatePlexFlowAsync(
            this LogicContext context,
            PlexChallengeCreationData challenge,
            PlexPinData pin,
            string clientIdentifier,
            CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."Id"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = 'plex'
                    AND provider."IsEnabled" = TRUE
                FOR SHARE OF
                    provider;
                """;
            PlexProviderData provider;
            try
            {
                provider = await context.ReadForUpdateAsync<PlexProviderData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                throw new SecurityException("Plex sign-in is not configured.");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountAuthChallenges" ("PublicId", "AuthChallengePurposeTypeId", "TokenHash", "ProtectedState", "ExpiresAt")
                VALUES
                    (@PublicId, @AuthChallengePurposeTypeId, @TokenHash, @ProtectedState, @ExpiresAt)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(challenge);
            var created = await context.ExecuteReturningAsync<AuthChallengeIdData>(sql, cancellationToken);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountExternalAuthFlows" ("AccountAuthChallengeId", "AuthChallengePurposeTypeId", "ProviderId", "ExternalPinId", "ClientIdentifier")
                VALUES
                    (@AccountAuthChallengeId, @AuthChallengePurposeTypeId, @ProviderId, @ExternalPinId, @ClientIdentifier);
                """;
            sql.Parameters.Add(new PlexFlowCreationData(created.Id, provider.Id, pin.Id, clientIdentifier));
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new InvalidOperationException("The external authentication attempt could not be created.");
            }
        }

        public static async Task<PlexFlowData> ReadPlexFlowForUpdateAsync(
            this LogicContext context,
            PlexChallengeParameters parameters,
            CancellationToken cancellationToken)
        {
            parameters.Validate();
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    challenge."Id",
                    flow."ProviderId",
                    flow."ExternalPinId",
                    flow."ClientIdentifier",
                    challenge."TokenHash",
                    challenge."ProtectedState"
                FROM
                    "AccountAuthChallenges" AS challenge
                INNER JOIN
                    "AccountExternalAuthFlows" AS flow ON flow."AccountAuthChallengeId" = challenge."Id"
                        AND flow."AuthChallengePurposeTypeId" = challenge."AuthChallengePurposeTypeId"
                INNER JOIN
                    "Providers" AS provider ON provider."Id" = flow."ProviderId"
                WHERE
                    challenge."PublicId" = @AuthChallengePublicId
                    AND challenge."AuthChallengePurposeTypeId" = 7
                    AND challenge."ConsumedAt" IS NULL
                    AND challenge."ExpiresAt" > statement_timestamp()
                    AND challenge."ProtectedState" IS NOT NULL
                    AND provider."Key" = 'plex'
                    AND provider."IsEnabled" = TRUE
                FOR UPDATE OF
                    challenge, flow
                FOR SHARE OF
                    provider;
                """;
            sql.Parameters.Add(parameters);

            PlexFlowData flow;
            try
            {
                flow = await context.ReadForUpdateAsync<PlexFlowData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                throw new UnauthorizedAccessException("The authentication attempt is unavailable.");
            }

            var proof = SHA256.HashData(Encoding.UTF8.GetBytes(parameters.BrowserNonce));
            if (!CryptographicOperations.FixedTimeEquals(proof, flow.TokenHash))
            {
                throw new UnauthorizedAccessException("The authentication attempt is unavailable.");
            }

            return flow;
        }

        public static async Task<PlexCompletionData> CompletePlexAsync(
            this LogicContext context,
            PlexChallengeParameters parameters,
            PlexIdentityData identity,
            PlexRegistrationData registration,
            TimeSpan sessionLifetime,
            CancellationToken cancellationToken)
        {
            var flow = await context.ReadPlexFlowForUpdateAsync(parameters, cancellationToken);
            var external = new ExternalLoginParameters(flow.ProviderId, identity.ExternalAccountId);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    account."Id",
                    account."CredentialRevision",
                    NULL::text AS "PasswordHash",
                    account."IsEnabled",
                    EXISTS (
                        SELECT 1
                        FROM "AccountTotpFactors" AS factor
                        WHERE factor."AccountId" = account."Id"
                            AND factor."ConfirmedAt" IS NOT NULL
                            AND factor."RevokedAt" IS NULL
                    ) AS "RequiresSecondFactor"
                FROM
                    "AccountExternalLogins" AS identity
                INNER JOIN
                    "Accounts" AS account ON account."Id" = identity."AccountId"
                WHERE
                    identity."ProviderId" = @ProviderId
                    AND identity."ExternalAccountId" = @ExternalAccountId
                FOR UPDATE OF
                    identity, account;
                """;
            sql.Parameters.Add(external);

            AccountAuthenticationData? account = null;
            try
            {
                account = await context.ReadForUpdateAsync<AccountAuthenticationData>(sql, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                // A new identity must qualify independently; email similarity never links accounts.
            }

            if (account is null)
            {
                if (!registration.HasLibraryAccess || !identity.EmailVerified)
                {
                    throw new SecurityException("Verified email and connected library access are required.");
                }

                var creation = new AccountCreationData(identity.Email, identity.DisplayName, !registration.RequiresApproval, DateTime.UtcNow);
                var id = await context.CreateAccountAsync(creation, registration.UiLocaleId, cancellationToken)
                    ?? throw new SecurityException("An existing account must be linked explicitly.");

                sql = new SqlStatement();
                sql.Statement =
                    """
                    INSERT INTO
                        "AccountExternalLogins" ("ProviderId", "ExternalAccountId", "AccountId")
                    VALUES
                        (@ProviderId, @ExternalAccountId, @AccountId)
                    ON CONFLICT ("ProviderId", "ExternalAccountId") DO NOTHING;
                    """;
                sql.Parameters.Add(new ExternalLoginData(flow.ProviderId, identity.ExternalAccountId, id));
                if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
                {
                    throw new UnauthorizedAccessException("The external identity changed.");
                }

                account = new AccountAuthenticationData(id, 1, null, !registration.RequiresApproval, false);
            }

            if (account.RequiresSecondFactor)
            {
                throw new UnauthorizedAccessException("A second factor is required.");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AccountAuthChallenges"
                SET
                    "ConsumedAt" = now(),
                    "ProtectedState" = NULL
                WHERE
                    "PublicId" = @AuthChallengePublicId
                    AND "ConsumedAt" IS NULL;
                """;
            sql.Parameters.Add(parameters);
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new UnauthorizedAccessException("The authentication attempt is unavailable.");
            }

            if (!account.IsEnabled)
            {
                return new PlexCompletionData(null, requiresApproval: true);
            }

            var session = await context.CreateSessionAsync(account, sessionLifetime, cancellationToken);
            return new PlexCompletionData(session, requiresApproval: false);
        }
    }
}
