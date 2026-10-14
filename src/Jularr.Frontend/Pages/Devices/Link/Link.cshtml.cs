using System.Text.RegularExpressions;
using Jularr.Backend.Data.Public.Device.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Device.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Devices.Link
{
    // A TV or another browser without a keyboard: it shows a code and a QR code, a signed-in phone confirms it, and this page signs in.
    // The pairing belongs to the browser that asked for it (a strict cookie), so no link can sign someone else in.
    [EnableRateLimiting(ServiceApi.DevicePairingRateLimit)]
    public sealed partial class LinkModel(StartDevicePairingV1 start, ClaimDevicePairingV1 claim, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private string? _code;

        protected override void Define(Page page)
        {
            page.Title("devices.link.title");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (_code is null)
            {
                page.Content.Add(new Form<StartDevicePairingParametersV1>(form => form.Submit("devices.link.start"), "/Devices/Link", csrf, new StartDevicePairingParametersV1()));
                return;
            }

            page.Content.Add(new P(new Text("devices.link.hint")));
            page.Content.Add(new P(new ValueText(_code)).Attribute("aria-live", "polite"));
            page.Content.Add(new QrCode(Request.Scheme + "://" + Request.Host + "/Devices/Pair?code=" + _code, "devices.link.qr"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.Can(null, "Devices"))
            {
                return NotFound();
            }

            if (Pairing() is { } pairing)
            {
                DevicePairingClaimV1? result;
                try
                {
                    result = await claim.ExecuteAsync<DevicePairingClaimV1>(new ClaimDevicePairingParametersV1(pairing.Id), default, HttpContext.RequestAborted);
                }
                catch (KeyNotFoundException)
                {
                    result = null;
                }

                if (result is { IsPaired: true })
                {
                    Response.Cookies.Delete(ServiceApi.DevicePairingCookieName, CookieOptions());
                    await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
                    return LocalRedirect("/");
                }

                // Looks again every few seconds until the phone confirmed; an expired code asks for a new one.
                if (result is not null)
                {
                    Response.Headers["Refresh"] = "5";
                    _code = pairing.Code;
                }
                else
                {
                    Response.Cookies.Delete(ServiceApi.DevicePairingCookieName, CookieOptions());
                }
            }

            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var started = await start.ExecuteAsync<DevicePairingStartedV1>(new StartDevicePairingParametersV1(), new StartDevicePairingDataV1(texts["devices.link.deviceName"], "Web"),
                HttpContext.RequestAborted);
            var options = CookieOptions();
            options.Expires = started.ExpiresAt;
            Response.Cookies.Append(ServiceApi.DevicePairingCookieName, started.PairingId.ToString("D") + "." + started.Code, options);
            return LocalRedirect("/Devices/Link");
        }

        private (Guid Id, string Code)? Pairing()
        {
            var match = Request.Cookies[ServiceApi.DevicePairingCookieName] is { } value ? PairingValue().Match(value) : Match.Empty;
            return match.Success ? (Guid.Parse(match.Groups["id"].Value), match.Groups["code"].Value) : null;
        }

        private static CookieOptions CookieOptions() => new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/Devices/Link" };

        [GeneratedRegex("^(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\\.(?<code>[0-9]{6})$")]
        private static partial Regex PairingValue();
    }
}
