using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Service.User.Device.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Devices.Pair
{
    // The code a TV or app shows, confirmed for the signed-in profile; the QR code opens this page with it filled in.
    public sealed class PairModel(ApproveDevicePairingV1 approve, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private ApproveDevicePairingDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public string? Code { get; set; }

        protected override void Define(Page page)
        {
            page.Title("devices.pair.title");
            page.Navigation("nav.settings");
            if (TempData["DevicePaired"] is true) page.Content.Add(new P(new Text("devices.pair.done")).Attribute("role", "status"));
            page.Content.Add(new Form<ApproveDevicePairingDataV1>(form =>
            {
                form.Text(item => item.Code, "devices.pair.code", "one-time-code");
                form.Submit("devices.pair.submit");
            }, "/Devices/Pair", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input ?? new ApproveDevicePairingDataV1(Code ?? ""), _input is null ? null : FormError));
        }

        public IActionResult OnGet()
        {
            if (!texts.Can("profile.devices", "Devices"))
            {
                return NotFound();
            }

            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            _input = new ApproveDevicePairingDataV1(Code ?? "");
            await approve.ExecuteAsync<UpdateResult>(new ApproveDevicePairingParametersV1(), _input, HttpContext.RequestAborted);
            TempData["DevicePaired"] = true;
            return LocalRedirect("/Devices/Pair");
        }
    }
}
