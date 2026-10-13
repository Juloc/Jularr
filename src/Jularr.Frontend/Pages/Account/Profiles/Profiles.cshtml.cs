using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account.Profiles
{
    public sealed class ProfilesModel(ReadProfilesV1 readProfiles, SelectProfileV1 selectProfile, IAntiforgery antiforgery, FrontendAssets assets) : JularrPage
    {
        private PageResult<ProfileSelectionDetailsV1>? _profiles;
        private string _returnUrl = "/";
        private int _pageNumber = 1;

        protected override void Define(Page page)
        {
            page.Shell = false;
            page.Title("account.profile.title");
            page.Asset("Pages/Account/Login/Login.entry.scss");
            var content = new List<IUiComponent> { new Brand(assets, login: true), new H1(new Text("account.profile.title")) };
            if (FormError is not null) content.Add(new P(new Text(FormError.ResourceKey)).Class("field-error").Attribute("role", "alert"));

            // Render authorized profile choices.
            if (_profiles is not null)
            {
                foreach (var profile in _profiles.Items)
                {
                    if (profile.IsLocked)
                    {
                        content.Add(new A(new ValueText(profile.DisplayName), new Icon("key", assets), new Span(new Text("account.profile.locked")).Class("visually-hidden"))
                            .Attribute("href", Url.Page("/Account/ProfilePin/ProfilePin", new { profileId = profile.Id, returnUrl = _returnUrl })!).Class("button profile-locked"));
                        continue;
                    }

                    content.Add(new Form<SelectProfileParametersV1>(form =>
                    {
                        form.Hidden(x => x.ProfileId);
                        form.Submit("account.profile.choose", profile.DisplayName);
                    }, Url.Page("/Account/Profiles/Profiles", new { returnUrl = _returnUrl }), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, new SelectProfileParametersV1(profile.Id)));
                }

                if (_profiles.Items.Count == 0) content.Add(new P(new Text("account.profile.empty")).Attribute("role", "status"));
                if (_profiles.Page > 1)
                {
                    content.Add(new A(new Text("common.previous")).Attribute("href", Url.Page("/Account/Profiles/Profiles", new { pageNumber = _profiles.Page - 1, returnUrl = _returnUrl })!));
                }

                if (_profiles.Items.Count == _profiles.PageSize)
                {
                    content.Add(new A(new Text("common.next")).Attribute("href", Url.Page("/Account/Profiles/Profiles", new { pageNumber = _profiles.Page + 1, returnUrl = _returnUrl })!));
                }
            }

            content.Add(new A(new Text("account.logout.title")).Attribute("href", "/Account/Logout"));
            page.Content.Add(new Section([.. content]).Class("auth-card profile-picker"));
        }

        public async Task<IActionResult> OnGetAsync(string? returnUrl, int pageNumber = 1)
        {
            _returnUrl = SafeReturnUrl(returnUrl);
            _pageNumber = pageNumber;
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long profileId, string? returnUrl)
        {
            _returnUrl = SafeReturnUrl(returnUrl);

            // Select, then apply committed proof.
            var result = await selectProfile.ExecuteAsync<SelectProfileResultV1>(new SelectProfileParametersV1(profileId), default, HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
            return LocalRedirect(_returnUrl);
        }

        protected override async Task LoadDataAsync()
        {
            _profiles = await readProfiles.ExecuteAsync<PageResult<ProfileSelectionDetailsV1>>(new ReadProfilesParametersV1(_pageNumber), HttpContext.RequestAborted);
        }

        private string SafeReturnUrl(string? returnUrl) => Url.IsLocalUrl(returnUrl) && !returnUrl!.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) ? returnUrl : "/";
    }
}
