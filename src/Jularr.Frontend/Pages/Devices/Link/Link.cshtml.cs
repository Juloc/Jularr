using Jularr.Backend.Data.Public.Device.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Device.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Devices.Link
{
    // A TV or another browser without a keyboard: it shows a code and a QR code, a signed-in phone confirms it, and this page signs in.
    public sealed class LinkModel(StartDevicePairingV1 start, ClaimDevicePairingV1 claim, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private string? _code;

        protected override void Define(Page page)
        {
            page.Title("devices.link.title");
            var address = Request.Scheme + "://" + Request.Host + "/Devices/Pair?code=" + _code;
            page.Content.Add(new P(new Text("devices.link.hint")));
            page.Content.Add(new P(new ValueText(_code ?? "")).Class("pairing-code").Attribute("aria-live", "polite"));
            page.Content.Add(new QrCode(address, "devices.link.qr"));
        }

        public async Task<IActionResult> OnGetAsync(Guid? pairing, string? code)
        {
            if (!texts.Can(null, "Devices"))
            {
                return NotFound();
            }

            // A new code first; an expired one starts over.
            if (pairing is not { } pairingId || code is null)
            {
                var started = await start.ExecuteAsync<DevicePairingStartedV1>(new StartDevicePairingParametersV1(), new StartDevicePairingDataV1(texts["devices.link.deviceName"], "Web"),
                    HttpContext.RequestAborted);
                return LocalRedirect("/Devices/Link?pairing=" + started.PairingId.ToString("D") + "&code=" + started.Code);
            }

            DevicePairingClaimV1 result;
            try
            {
                result = await claim.ExecuteAsync<DevicePairingClaimV1>(new ClaimDevicePairingParametersV1(pairingId), default, HttpContext.RequestAborted);
            }
            catch (KeyNotFoundException)
            {
                return LocalRedirect("/Devices/Link");
            }

            if (result.IsPaired)
            {
                await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
                return LocalRedirect("/");
            }

            // Looks again every few seconds until the phone confirmed.
            Response.Headers["Refresh"] = "3";
            _code = code;
            PreparePage();
            return Page();
        }
    }
}
