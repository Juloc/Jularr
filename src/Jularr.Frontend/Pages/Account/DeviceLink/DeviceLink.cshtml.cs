using System.Text.RegularExpressions;
using Jularr.Backend.Data.Public.Device.V1;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Device.V1;
using Jularr.Backend.Service.Public.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Account.DeviceLink
{
    // A TV or another browser without a keyboard: it shows a code and a QR code, a signed-in phone confirms it, and this page signs in.
    // The pairing belongs to the browser that asked for it (a strict cookie), so no link can sign someone else in.
    [EnableRateLimiting(ServiceApi.DevicePairingRateLimit)]
    public sealed partial class DeviceLinkModel(StartDevicePairingV1 start, ClaimDevicePairingV1 claim, ReadInstancePresentationV1 presentation, IAntiforgery antiforgery) : JularrPage
    {
        private string? _code;
        private string? _pairAddress;

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
            if (_pairAddress is { } address)
            {
                page.Content.Add(new QrCode(address + "Devices/Pair?code=" + _code, "devices.link.qr"));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // A TV has no account yet, so the public presentation tells whether pairing is on.
            var instance = await presentation.ExecuteAsync<InstancePresentationDetailsV1>(new ReadInstancePresentationParametersV1(), HttpContext.RequestAborted);
            if (!instance.PairsDevices)
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
                    // An expired code is replaced by a new one; the old one ends at once.
                    return await StartAsync(pairing.Id);
                }

                if (result.IsPaired)
                {
                    Response.Cookies.Delete(ServiceApi.DevicePairingCookieName, CookieOptions());
                    await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
                    return LocalRedirect("/");
                }

                // Looks again every few seconds until the phone confirmed.
                Response.Headers["Refresh"] = "5";
                _code = pairing.Code;
                _pairAddress = PairAddress(instance.PublicUrl);
            }

            PreparePage();
            return Page();
        }

        public Task<IActionResult> OnPostAsync() => StartAsync(Pairing()?.Id);

        private async Task<IActionResult> StartAsync(Guid? replaces)
        {
            var started = await start.ExecuteAsync<DevicePairingStartedV1>(new StartDevicePairingParametersV1(),
                new StartDevicePairingDataV1(null, "Web", replaces), HttpContext.RequestAborted);
            // A browser cookie: an expired code must still be found to be replaced.
            Response.Cookies.Append(ServiceApi.DevicePairingCookieName, started.PairingId.ToString("D") + "." + started.Code, CookieOptions());
            return LocalRedirect("/Devices/Link");
        }

        // The configured public address, else this request's own if it names a proper host; none, and only the code is shown.
        private string? PairAddress(string? publicUrl) => publicUrl
            ?? (Request.Scheme is "http" or "https" && Uri.CheckHostName(Request.Host.Host) != UriHostNameType.Unknown ? Request.Scheme + "://" + Request.Host + "/" : null);

        private (Guid Id, string Code)? Pairing()
        {
            var match = Request.Cookies[ServiceApi.DevicePairingCookieName] is { } value ? PairingValue().Match(value) : Match.Empty;
            return match.Success ? (Guid.Parse(match.Groups["id"].Value), match.Groups["code"].Value) : null;
        }

        private CookieOptions CookieOptions() => new() { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/Devices/Link" };

        [GeneratedRegex("^(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\\.(?<code>[0-9]{6})$")]
        private static partial Regex PairingValue();
    }
}
