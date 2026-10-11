using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Jularr.Backend.Service.Core
{
    public sealed class SessionCookieEvents : CookieAuthenticationEvents
    {
        private readonly SessionAuthentication _authentication;

        public SessionCookieEvents(SessionAuthentication authentication)
        {
            _authentication = authentication;
        }

        public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            try
            {
                await _authentication.VerifyCookieAsync(context.HttpContext, context.Principal, context.HttpContext.RequestAborted);
            }
            catch (UnauthorizedAccessException)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
            }
        }
    }
}
