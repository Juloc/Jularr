using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Service.User.Device.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.DevicePair
{
    // A code a TV or app shows: first the device it belongs to is shown, then it is confirmed for the signed-in profile.
    // The QR code opens this page with the code filled in.
    public sealed class DevicePairModel(ReadDevicePairingV1 read, ApproveDevicePairingV1 approve, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private DevicePairingRequestV1? _device;

        [BindProperty(SupportsGet = true)]
        public string? Code { get; set; }

        protected override void Define(Page page)
        {
            page.Title("devices.pair.title");
            page.Navigation("nav.settings");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (TempData["DevicePaired"] is true) page.Content.Add(new P(new Text("devices.pair.done")).Attribute("role", "status"));
            if (_device is { } device)
            {
                page.Content.Add(new P(new ValueText(string.Format(CultureInfo.InvariantCulture, texts["devices.pair.device"], device.DeviceName, device.DevicePlatform, Code))));
                page.Content.Add(new Form<ApproveDevicePairingDataV1>(form =>
                {
                    form.Hidden(item => item.Code);
                    form.Submit("devices.pair.submit");
                }, "/Devices/Pair", csrf, new ApproveDevicePairingDataV1(Code ?? "")));
                return;
            }

            page.Content.Add(new Form<ApproveDevicePairingDataV1>(form =>
            {
                form.Text(item => item.Code, "devices.pair.code", "one-time-code");
                form.Submit("devices.pair.show");
            }, "/Devices/Pair?handler=Show", csrf, new ApproveDevicePairingDataV1(Code ?? ""), FormError));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.Can("profile.devices", "Devices"))
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(Code))
            {
                _device = await read.ExecuteAsync<DevicePairingRequestV1>(new DevicePairingCodeParametersV1(Code), HttpContext.RequestAborted);
            }

            PreparePage();
            return Page();
        }

        public IActionResult OnPostShow() => LocalRedirect("/Devices/Pair?code=" + Uri.EscapeDataString(Code ?? ""));

        public async Task<IActionResult> OnPostAsync()
        {
            await approve.ExecuteAsync<UpdateResult>(new ApproveDevicePairingParametersV1(), new ApproveDevicePairingDataV1(Code ?? ""), HttpContext.RequestAborted);
            TempData["DevicePaired"] = true;
            return LocalRedirect("/Devices/Pair");
        }
    }
}
