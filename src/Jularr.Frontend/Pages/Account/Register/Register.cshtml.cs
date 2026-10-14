using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Instance.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Account.Register
{
    // A visitor asks for an account; it stays disabled until an administrator enables it.
    [AllowAnonymous]
    [EnableRateLimiting("public")]
    public sealed class RegisterModel(ReadInstancePresentationV1 presentation, ReadUiLocalesV1 readLocales, RegisterV1 register, IAntiforgery antiforgery, FrontendAssets assets)
        : JularrPage
    {
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private RegisterDataV1? _input;

        protected override void Define(Page page)
        {
            page.Shell = false;
            page.Title("account.register.title");
            page.Asset("Pages/Account/Login/Login.entry.scss");
            var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture), locale.Name));
            page.Content.Add(new Section(
                new Brand(assets, login: true),
                new H1(new Text("account.register.title")),
                TempData["Registered"] is true
                    ? new P(new Text("account.register.pending")).Attribute("role", "status")
                    : new Form<RegisterDataV1>(form =>
                    {
                        form.Text(x => x.Email, "account.field.email", "email");
                        form.Text(x => x.DisplayName, "profile.field.displayName", "nickname");
                        form.Password(x => x.Password, "account.field.password", "new-password");
                        form.Select(x => x.UiLocaleId, "profile.field.uiLocale", options);
                        form.Submit("account.register.submit");
                    }, Url.Page("/Account/Register/Register")!, antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input, FormError),
                new A(new Text("account.login.title")).Attribute("href", "/Account/Login")).Class("auth-card"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!(await presentation.ExecuteAsync<InstancePresentationDetailsV1>(new ReadInstancePresentationParametersV1(), HttpContext.RequestAborted)).AllowsRegistration)
            {
                return NotFound();
            }

            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? email, string? displayName, string? password, long uiLocaleId)
        {
            _input = new RegisterDataV1(email ?? "", displayName ?? "", "", uiLocaleId);
            await register.ExecuteAsync<RegisterResultV1>(new AuthenticationParametersV1(), new RegisterDataV1(email ?? "", displayName ?? "", password ?? "", uiLocaleId), HttpContext.RequestAborted);
            TempData["Registered"] = true;
            return RedirectToPage("/Account/Register/Register");
        }

        protected override async Task LoadDataAsync()
        {
            _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
            _input ??= new RegisterDataV1("", "", "", _locales.FirstOrDefault()?.Id ?? 0);
        }
    }
}
