using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Session;
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

        internal async Task<SessionAuthenticationData> VerifyAsync(HttpContext http, ClaimsPrincipal? principal, CancellationToken cancellationToken, DateTime? renewalExpiresAt = null)
        {
            var session = await ReadAsync(principal, cancellationToken, renewalExpiresAt);
            http.Items[typeof(SessionAuthentication)] = (principal, session, Parameters!);
            return session;
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

        public async Task<SessionAuthenticationData> ReadAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken, DateTime? renewalExpiresAt = null)
        {
            Parameters = null;

            // Validate protected cookie claims or a device token.
            var identities = principal?.Identities.Where(identity => identity.IsAuthenticated
                && identity.AuthenticationType is CookieAuthenticationDefaults.AuthenticationScheme or ServiceApi.DeviceBearerScheme).ToArray();
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

            var bearer = identities[0].AuthenticationType == ServiceApi.DeviceBearerScheme;

            // Verify current database state.
            var parameters = new SessionParameters(sessionId, revision, SHA256.HashData(Encoding.UTF8.GetBytes(tokens[0].Value)));

            // Renew within verified locks.
            if (renewalExpiresAt is { } expiresAt)
            {
                await using var transaction = new SqlContext(_database);
                await transaction.BeginAsync(SqlAccessMode.ReadWrite, cancellationToken: cancellationToken);
                var ctx = new LogicContext(transaction);
                var verified = await ctx.ReadSessionAsync(parameters, cancellationToken);
                if (bearer && !verified.IsDevice)
                {
                    throw new UnauthorizedAccessException("Only device sessions sign in by token.");
                }

                await ctx.RenewSessionAsync(parameters, new SessionRenewalData(expiresAt), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                Parameters = parameters;
                return verified;
            }

            // Ordinary requests remain read-only.
            await using var context = new SqlContext(_database);
            await context.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: cancellationToken);
            var session = await new LogicContext(context).ReadSessionProofAsync(parameters, cancellationToken);
            if (bearer && !session.IsDevice)
            {
                throw new UnauthorizedAccessException("Only device sessions sign in by token.");
            }

            Parameters = parameters;
            return session;
        }
    }
}
