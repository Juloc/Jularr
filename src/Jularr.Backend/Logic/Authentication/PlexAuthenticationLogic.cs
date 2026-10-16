using System.Security;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Logic.Authentication;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Data.Logic.Provider;

namespace Jularr.Backend.Logic.Authentication
{
    internal static class PlexAuthenticationLogic
    {
        public static async Task CreatePlexFlowAsync(this LogicContext ctx, PlexChallengeCreationData challenge, long externalPinId, string clientIdentifier, CancellationToken ct)
        {
            // Lock the configured provider.
            var provider = await ctx.ReadEnabledProviderAsync(new ProviderParameters("plex"), ct);

            // Store the one-time challenge.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountAuthChallenges" ("PublicId", "AuthChallengePurposeTypeId", "TokenHash", "ProtectedState", "ExpiresAt", "AccountId", "CredentialRevision")
                SELECT
                    @PublicId,
                    @AuthChallengePurposeTypeId,
                    @TokenHash,
                    @ProtectedState,
                    @ExpiresAt,
                    @AccountId,
                    account."CredentialRevision"
                FROM
                    (SELECT 1) AS input
                    LEFT JOIN "Accounts" AS account ON account."Id" = @AccountId
                WHERE
                    @AccountId::bigint IS NULL
                    OR account."IsEnabled"
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(challenge);
            var created = await ctx.ReadRequiredAsync<AuthChallengeIdData>(sql, ct);

            // Link the external attempt.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AccountExternalAuthFlows" ("AccountAuthChallengeId", "AuthChallengePurposeTypeId", "ProviderId", "ExternalPinId", "ClientIdentifier")
                VALUES
                    (@AccountAuthChallengeId, @AuthChallengePurposeTypeId, @ProviderId, @ExternalPinId, @ClientIdentifier);
                """;
            sql.Parameters.Add(new PlexFlowCreationData(created.Id, provider.Id, externalPinId, clientIdentifier, challenge.AuthChallengePurposeTypeId));
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The external authentication attempt could not be created.");
            }
        }

        public static async Task<PlexFlowData> ReadPlexFlowAsync(this LogicContext ctx, PlexChallengeParameters parameters, CancellationToken ct)
        {
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
                    AND challenge."AuthChallengePurposeTypeId" = @AuthChallengePurposeTypeId
                    AND challenge."AccountId" IS NOT DISTINCT FROM @AccountId
                    AND (challenge."AccountId" IS NULL OR EXISTS (
                        SELECT
                            1
                        FROM
                            "Accounts" AS account
                        WHERE
                            account."Id" = challenge."AccountId"
                            AND account."IsEnabled"
                            AND account."CredentialRevision" = challenge."CredentialRevision"
                    ))
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

            var flow = await ctx.ReadOptionalAsync<PlexFlowData>(sql, ct)
                ?? throw new UnauthorizedAccessException("The authentication attempt is unavailable.");

            // Verify the browser nonce.
            var proof = SHA256.HashData(Encoding.UTF8.GetBytes(parameters.BrowserNonce));
            if (!CryptographicOperations.FixedTimeEquals(proof, flow.TokenHash))
            {
                throw new UnauthorizedAccessException("The authentication attempt is unavailable.");
            }

            return flow;
        }

        public static async Task<PlexCompletionData> CompletePlexAsync(this LogicContext ctx, PlexChallengeParameters parameters, PlexIdentityData identity, PlexRegistrationData registration, TimeSpan sessionLifetime, CancellationToken ct)
        {
            // Lock and revalidate completion.
            var flow = await ctx.ReadPlexFlowAsync(parameters, ct);
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

            // Never link by email.
            var account = await ctx.ReadOptionalAsync<AccountAuthenticationData>(sql, ct);

            // Qualify a new identity.
            if (account is null)
            {
                if (!registration.HasLibraryAccess || !identity.EmailVerified)
                {
                    throw new SecurityException("Verified email and connected library access are required.");
                }

                var creation = new AccountCreationData(identity.Email, identity.DisplayName, !registration.RequiresApproval, DateTime.UtcNow);
                var id = await ctx.CreateAccountAsync(creation, registration.UiLocaleId, ct)
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
                if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
                {
                    throw new UnauthorizedAccessException("The external identity changed.");
                }

                account = new AccountAuthenticationData(id, 1, null, !registration.RequiresApproval, false);
            }

            // Require completed second-factor verification.
            if (account.RequiresSecondFactor)
            {
                throw new UnauthorizedAccessException("A second factor is required.");
            }

            await ctx.ConsumePlexFlowAsync(parameters, ct);

            // Pending accounts receive no session.
            if (!account.IsEnabled)
            {
                return new PlexCompletionData(null, requiresApproval: true);
            }

            var session = await ctx.CreateSessionAsync(account, sessionLifetime, ct);
            return new PlexCompletionData(session, requiresApproval: false);
        }

        public static async Task ConsumePlexFlowAsync(this LogicContext ctx, PlexChallengeParameters parameters, CancellationToken ct)
        {
            _ = await ctx.ReadPlexFlowAsync(parameters, ct);
            var sql = new SqlStatement();
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
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new UnauthorizedAccessException("The authentication attempt is unavailable.");
        }
    }
}
