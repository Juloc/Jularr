using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account.ProfileNew
{
    public sealed class ProfileNewModel(ReadAuthenticationV1 authentication, ReadUiLocalesV1 readLocales, CreateProfileV1 createProfile, IAntiforgery antiforgery, FrontendAssets assets, UiText texts)
        : JularrPage
    {
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private CreateProfileDataV1? _input;
        private string _returnUrl = "/";

        protected override void Define(Page page)
        {
            page.Shell = false;
            page.Title("account.profile.add");
            page.Asset("Pages/Account/Login/Login.entry.scss");
            var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
            page.Content.Add(new Section(
                new Brand(assets, login: true),
                new H1(new Text("account.profile.add")),
                new Form<CreateProfileDataV1>(form =>
                {
                    form.Text(x => x.DisplayName, "profile.field.displayName", "nickname");
                    form.Select(x => x.UiLocaleId, "profile.field.uiLocale", options);
                    form.Submit("account.profile.create");
                }, Url.Page("/Account/ProfileNew/ProfileNew", new { returnUrl = _returnUrl }), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input, FormError),
                new A(new Text("common.back")).Attribute("href", Url.Page("/Account/Profiles/Profiles", new { returnUrl = _returnUrl })!)).Class("auth-card"));
        }

        public async Task<IActionResult> OnGetAsync(string? returnUrl)
        {
            if (!texts.CanCreateProfiles())
            {
                return NotFound();
            }

            _returnUrl = SafeReturnUrl(returnUrl);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? displayName, long uiLocaleId, string? returnUrl)
        {
            _returnUrl = SafeReturnUrl(returnUrl);
            _input = new CreateProfileDataV1(displayName ?? "", uiLocaleId);
            await createProfile.ExecuteAsync<UpdateResult>(new ReadProfilesParametersV1(), _input, HttpContext.RequestAborted);
            return RedirectToPage("/Account/Profiles/Profiles", new { returnUrl = _returnUrl });
        }

        protected override async Task LoadDataAsync()
        {
            // The new profile starts in the current language.
            var token = HttpContext.RequestAborted;
            _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: token);
            var current = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: token)).UiLocaleId;
            _input ??= new CreateProfileDataV1("", current ?? _locales.FirstOrDefault()?.Id ?? 0);
        }

        private string SafeReturnUrl(string? returnUrl) => Url.IsLocalUrl(returnUrl) && !returnUrl!.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) ? returnUrl : "/";
    }
}
