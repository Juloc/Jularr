using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    internal sealed class SessionAuthentication
    {
        private readonly NpgsqlDataSource _database;

        public SessionAuthentication(NpgsqlDataSource database)
        {
            _database = database;
        }

        public SessionParameters? Parameters { get; private set; }

        internal async Task VerifyCookieAsync(HttpContext http, ClaimsPrincipal? principal, CancellationToken cancellationToken)
        {
            var session = await ReadAsync(principal, cancellationToken);
            http.Items[typeof(SessionAuthentication)] = (principal, session, Parameters!);
        }

        public Task<SessionAuthenticationData> ReadRequestAsync(HttpContext? http, CancellationToken cancellationToken)
        {
            // Reuse middleware proof only.
            if (http?.Items[typeof(SessionAuthentication)] is
                ValueTuple<ClaimsPrincipal?, SessionAuthenticationData, SessionParameters> proof
                && ReferenceEquals(proof.Item1, http.User))
            {
                Parameters = proof.Item3;
                return Task.FromResult(proof.Item2);
            }

            // Direct calls revalidate sessions.
            return ReadAsync(http?.User, cancellationToken);
        }

        public async Task<SessionAuthenticationData> ReadAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken)
        {
            Parameters = null;

            // Validate protected cookie claims.
            var identities = principal?.Identities.Where(identity => identity.IsAuthenticated
                && identity.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme).ToArray();
            var ids = identities?.SelectMany(identity => identity.FindAll(ServiceGate.SessionClaimType)).ToArray();
            var revisions = identities?.SelectMany(identity => identity.FindAll(ServiceGate.SessionRevisionClaimType)).ToArray();
            var tokens = identities?.SelectMany(identity => identity.FindAll(ServiceGate.SessionTokenClaimType)).ToArray();

            if (identities?.Length != 1 || ids?.Length != 1 || revisions?.Length != 1 || tokens?.Length != 1
                || !Guid.TryParseExact(ids[0].Value, "D", out var sessionId) || sessionId == Guid.Empty
                || !long.TryParse(revisions[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0
                || tokens[0].Value.Length != 43 || tokens[0].Value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            {
                throw new UnauthorizedAccessException("A verified session is required.");
            }

            // Verify current database state.
            var parameters = new SessionParameters(sessionId, revision, SHA256.HashData(Encoding.UTF8.GetBytes(tokens[0].Value)));
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
                LEFT JOIN
                    "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
                        AND membership."ProfileId" = session."ActiveProfileId"
                LEFT JOIN
                    "Profiles" AS profile ON profile."Id" = membership."ProfileId"
                WHERE
                    session."PublicId" = @SessionPublicId
                    AND session."RotationRevision" = @SessionRevision
                    AND session."TokenHash" = @SessionTokenHash
                    AND session."RevokedAt" IS NULL
                    AND session."ExpiresAt" > statement_timestamp()
                    AND session."CredentialRevision" = account."CredentialRevision"
                    AND account."IsEnabled" = TRUE
                    AND (session."ActiveProfileId" IS NULL OR profile."Id" IS NOT NULL);
                """;
            sql.Parameters.Add(parameters);

            await using var context = new SqlContext(_database);
            await context.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: cancellationToken);
            var session = await context.ReadSql.ReadOptionalAsync<SessionAuthenticationData>(sql, cancellationToken)
                ?? throw new UnauthorizedAccessException("The session is unavailable.");

            Parameters = parameters;
            return session;
        }
    }
}
