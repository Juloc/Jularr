using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Jularr.Backend.Data.Logic.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // App device token: "Bearer <session>.<revision>.<secret>".
    internal sealed class DeviceBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        SessionAuthentication authentication, IOptions<AuthenticationOptions> authenticationOptions, TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        // Wrong tokens per address; once spent, the database is not asked for a minute.
        private static readonly PartitionedRateLimiter<string> Failures = PartitionedRateLimiter.Create<string, string>(address =>
            RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

        public static string Token(SessionCredentialsData session) =>
            session.PublicId.ToString("D") + "." + session.RotationRevision.ToString(CultureInfo.InvariantCulture) + "." + session.Token;

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var address = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            using (var available = Failures.AttemptAcquire(address, 0))
            {
                if (!available.IsAcquired)
                {
                    return AuthenticateResult.Fail("Too many wrong device tokens.");
                }
            }

            var header = Request.Headers.Authorization.ToString();
            var parts = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Split('.') : [];
            if (parts.Length != 3)
            {
                return Failed(address);
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ServiceGate.SessionClaimType, parts[0]),
                new Claim(ServiceGate.SessionRevisionClaimType, parts[1]),
                new Claim(ServiceGate.SessionTokenClaimType, parts[2])], ServiceApi.DeviceBearerScheme));
            try
            {
                // Renew after half-life only.
                var session = await authentication.VerifyAsync(Context, principal, Context.RequestAborted);
                var now = time.GetUtcNow().UtcDateTime;
                var lifetime = TimeSpan.FromHours(authenticationOptions.Value.SessionLifetimeHours);
                if (session.ExpiresAt - now < lifetime / 2)
                {
                    await authentication.VerifyAsync(Context, principal, Context.RequestAborted, now.Add(lifetime));
                }

                return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
            }
            catch (UnauthorizedAccessException)
            {
                return Failed(address);
            }
        }

        private static AuthenticateResult Failed(string address)
        {
            using var _ = Failures.AttemptAcquire(address);
            return AuthenticateResult.Fail("The device token is invalid.");
        }
    }
}
