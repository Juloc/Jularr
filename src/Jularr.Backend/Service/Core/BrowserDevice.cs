using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Service.Core
{
    // A browser's random identifier, made once and kept in a first-party cookie that signing out does not clear, so the choices of the device
    // (its playback quality) survive the next sign-in. It is a plain UUID: nothing about the browser is read to make or recompute it.
    internal static class BrowserDevice
    {
        public const string CookieName = "jularr.device";
        private static readonly object ItemKey = new();

        // The identifier of the asking browser; a browser without one gets a new one, which Issue stores with the next signed-in session.
        // Apps receive tokens, not cookies, and have none.
        public static Guid? Current(HttpContext? http)
        {
            if (http is null || ServiceApi.IsAppRequest(http))
            {
                return null;
            }

            if (http.Items[ItemKey] is Guid known)
            {
                return known;
            }

            var id = Guid.TryParseExact(http.Request.Cookies[CookieName], "D", out var stored) && stored != Guid.Empty ? stored : Guid.NewGuid();
            http.Items[ItemKey] = id;
            return id;
        }

        // Stores the identifier for a long time; every sign-in renews it. Sign-out never touches it.
        public static void Issue(HttpContext http)
        {
            if (Current(http) is { } id)
            {
                http.Response.Cookies.Append(CookieName, id.ToString("D"), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    IsEssential = true,
                    MaxAge = TimeSpan.FromDays(400)
                });
            }
        }
    }
}
