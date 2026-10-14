using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
        SessionAuthentication authentication, IOptions<AuthenticationOptions> authenticationOptions, TimeProvider time, DeviceTokenFailures failures) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public static string Token(SessionCredentialsData session) =>
            session.PublicId.ToString("D") + "." + session.RotationRevision.ToString(CultureInfo.InvariantCulture) + "." + session.Token;

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var address = DeviceTokenFailures.Key(Context.Connection.RemoteIpAddress);
            if (failures.IsBlocked(address))
            {
                return AuthenticateResult.Fail("Too many wrong device tokens.");
            }

            var header = Request.Headers.Authorization.ToString();
            var parts = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim().Split('.') : [];
            if (parts.Length != 3)
            {
                return failures.Fail(address);
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
                return failures.Fail(address);
            }
        }
    }

    // Wrong tokens per address; spent, the database is not asked for a minute.
    internal sealed class DeviceTokenFailures : IDisposable
    {
        private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(address =>
            RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

        // IPv6 clients usually own a whole /64.
        public static string Key(IPAddress? address) => address switch
        {
            null => "unknown",
            { IsIPv4MappedToIPv6: true } => address.MapToIPv4().ToString(),
            { AddressFamily: AddressFamily.InterNetworkV6 } => new IPNetwork(new IPAddress([.. address.GetAddressBytes()[..8], .. new byte[8]]), 64).ToString(),
            _ => address.ToString()
        };

        public bool IsBlocked(string address)
        {
            using var available = _limiter.AttemptAcquire(address, 0);
            return !available.IsAcquired;
        }

        public AuthenticateResult Fail(string address)
        {
            using var _ = _limiter.AttemptAcquire(address);
            return AuthenticateResult.Fail("The device token is invalid.");
        }

        public void Dispose() => _limiter.Dispose();
    }
}
