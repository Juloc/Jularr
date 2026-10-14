using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    internal sealed class SessionCookieEvents : CookieAuthenticationEvents
    {
        private readonly SessionAuthentication _authentication;
        private readonly AuthenticationOptions _options;
        private readonly TimeProvider _time;

        public SessionCookieEvents(SessionAuthentication authentication, IOptions<AuthenticationOptions> options, TimeProvider time)
        {
            _authentication = authentication;
            _options = options.Value;
            _time = time;
        }

        // Background requests learn they are signed out; only page visits go to the sign-in page, whose new antiforgery cookie would spoil an open form.
        public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
        {
            if (context.Request.Headers["Sec-Fetch-Mode"].ToString() is { Length: > 0 } mode && mode != "navigate")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            return base.RedirectToLogin(context);
        }

        public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            try
            {
                // Renew after half-life only.
                var now = _time.GetUtcNow();
                var lifetime = TimeSpan.FromHours(_options.SessionLifetimeHours);
                var renew = context.Properties.AllowRefresh != false && context.Properties.ExpiresUtc is { } expiry && expiry - now < lifetime / 2;
                var renewedExpiry = now.Add(lifetime);
                await _authentication.VerifyAsync(context.HttpContext, context.Principal, context.HttpContext.RequestAborted, renew ? renewedExpiry.UtcDateTime : null);

                // Publish after database commit.
                if (renew)
                {
                    context.Properties.IssuedUtc = now;
                    context.Properties.ExpiresUtc = renewedExpiry;
                    context.ShouldRenew = true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
            }
        }
    }
}
