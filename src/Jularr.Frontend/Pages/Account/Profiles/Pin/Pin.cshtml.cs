using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account.Profiles.Pin
{
    public sealed class PinModel(ReadProfilesV1 readProfiles, VerifyProfilePinV1 verifyPin, IAntiforgery antiforgery, FrontendAssets assets) : JularrPage
    {
        private ProfileSelectionDetailsV1? _profile;
        private long _profileId;
        private string _returnUrl = "/";

        protected override void Define(Page page)
        {
            page.Shell = false;
            page.Title("account.profile.pin");
            page.Asset("Pages/Account/Login/Login.entry.scss");
            page.Asset("Pages/Account/Profiles/Pin/Pin.entry.ts");
            var back = new A(new Text("common.back")).Attribute("href", Url.Page("/Account/Profiles/Profiles", new { returnUrl = _returnUrl })!);
            if (_profile is not { PinLength: { } length })
            {
                page.Content.Add(new Section(new Brand(assets, login: true), back).Class("auth-card"));
                return;
            }

            // Only the profile, its PIN and the way back.
            page.Content.Add(new Section(
                new Brand(assets, login: true),
                new H1(new ValueText(_profile.DisplayName)),
                new Form<VerifyProfilePinDataV1>(form =>
                {
                    form.Pin(x => x.Pin, "account.profile.pin", length);
                    form.Submit("account.profile.pin.submit");
                }, Url.Page("/Account/Profiles/Pin/Pin", new { profileId = _profileId, returnUrl = _returnUrl }), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, null, FormError),
                back).Class("auth-card"));
        }

        public async Task<IActionResult> OnGetAsync(long profileId, string? returnUrl)
        {
            _profileId = profileId;
            _returnUrl = SafeReturnUrl(returnUrl);
            await LoadDataAsync();
            if (_profile?.PinLength is null)
            {
                return RedirectToPage("/Account/Profiles/Profiles", new { returnUrl = _returnUrl });
            }

            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long profileId, string? returnUrl, string pin)
        {
            _profileId = profileId;
            _returnUrl = SafeReturnUrl(returnUrl);

            // Verify, then apply committed proof.
            var result = await verifyPin.ExecuteAsync<SelectProfileResultV1>(new SelectProfileParametersV1(profileId), new VerifyProfilePinDataV1(pin), HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
            return LocalRedirect(_returnUrl);
        }

        protected override async Task LoadDataAsync()
        {
            var profiles = await readProfiles.ExecuteAsync<PageResult<ProfileSelectionDetailsV1>>(new ReadProfilesParametersV1(1, PageRequest.MaximumPageSize), HttpContext.RequestAborted);
            _profile = profiles.Items.FirstOrDefault(profile => profile.Id == _profileId);
        }

        private string SafeReturnUrl(string? returnUrl) => Url.IsLocalUrl(returnUrl) && !returnUrl!.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) ? returnUrl : "/";
    }
}
