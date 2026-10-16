using Jularr.Backend.Service.Public.Instance.V1;
using Jularr.Backend.Data.Public.Instance.V1;
using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.Public.Setup.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Setup.Start
{
    [AllowAnonymous]
    [EnableRateLimiting("public")]
    public sealed class StartModel(ReadSetupV1 setup, CreateOwnerV1 createOwner, ReadSetupLocalesV1 readLocales, ReadInstancePresentationV1 readPresentation, IAntiforgery antiforgery, UiText texts)
        : JularrPage
    {
        private bool _requiresOwner;
        private IReadOnlyList<SetupLocaleV1> _locales = [];
        private CreateOwnerDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("setup.start.title");

            // Render only available setup.
            if (!_requiresOwner)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                page.Content.Add(new P(new Text("setup.owner.closed")));
                page.Content.Add(new A(new Text("account.login.submit")).Attribute("href", "/Account/Login"));
                return;
            }

            var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture),
                locale.IsMachineTranslated ? string.Format(CultureInfo.CurrentCulture, texts["setup.locale.machine"], locale.Name) : locale.Name));
            page.Content.Add(new UiSection("setup.owner.title", [new Form<CreateOwnerDataV1>(form =>
            {
                form.Text(item => item.Email, "account.field.email", "username");
                form.Text(item => item.DisplayName, "admin.accounts.name", "nickname");
                form.Password(item => item.Password, "account.field.password", "new-password");
                form.Password(item => item.PasswordConfirmation, "setup.owner.passwordConfirmation", "new-password", new FieldHelp(Info: ""));
                form.Select(item => item.UiLocaleId, "profile.field.uiLocale", options);
                form.Submit("setup.owner.create");
            }, Url.Page("/Setup/Start/Start"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input, FormError)]));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            if (!_requiresOwner) return User.Identity?.IsAuthenticated is true ? LocalRedirect("/Setup/Progress") : RedirectToPage("/Account/Login/Login");

            var instance = (await readPresentation.ExecuteAsync<InstancePresentationDetailsV1>(cancellationToken: HttpContext.RequestAborted)).UiLocaleId;
            _input = new CreateOwnerDataV1("", "", "", "", LocalePreference.Preferred(Request,
                _locales.Select(locale => new UiLocaleDetailsV1(locale.Id, locale.Locale, locale.Name, false)).ToArray(), instance));
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? email, string? displayName, string? password, string? passwordConfirmation, long uiLocaleId)
        {
            // Invoke the canonical bootstrap service.
            _input = new CreateOwnerDataV1(email ?? "", displayName ?? "", password ?? "", passwordConfirmation ?? "", uiLocaleId);
            var result = await createOwner.ExecuteAsync<LoginResultV1>(_input, HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);

            // Continue authenticated configuration.
            return LocalRedirect("/Setup/Progress");
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _requiresOwner = (await setup.ExecuteAsync<ReadSetupResultV1>(cancellationToken: token)).RequiresOwner;
            if (_requiresOwner)
            {
                _locales = await readLocales.ExecuteAsync<BaseDataList<SetupLocaleV1>>(cancellationToken: token);
            }
        }
    }
}
