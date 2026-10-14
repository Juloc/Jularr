using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Logic.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // Apps sign in with their paired device session: "Authorization: Bearer <session>.<revision>.<secret>"; other sessions are not accepted.
    internal sealed class DeviceBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        SessionAuthentication authentication, IOptions<AuthenticationOptions> sessions, TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public static string Token(SessionCredentialsData session) =>
            session.PublicId.ToString("D") + "." + session.RotationRevision.ToString(CultureInfo.InvariantCulture) + "." + session.Token;

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            var parts = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Split('.') : [];
            if (parts.Length != 3)
            {
                return AuthenticateResult.Fail("A device token is required.");
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ServiceGate.SessionClaimType, parts[0]),
                new Claim(ServiceGate.SessionRevisionClaimType, parts[1]),
                new Claim(ServiceGate.SessionTokenClaimType, parts[2])], ServiceApi.DeviceBearerScheme));
            try
            {
                // Every request renews the session, so a device in use stays paired.
                await authentication.VerifyAsync(Context, principal, Context.RequestAborted, time.GetUtcNow().AddHours(sessions.Value.SessionLifetimeHours).UtcDateTime);
                return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
            }
            catch (UnauthorizedAccessException)
            {
                return AuthenticateResult.Fail("The device session is unavailable.");
            }
        }
    }
}
