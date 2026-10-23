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
using Jularr.Frontend.Pages.Admin.Work;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Setup.Start
{
    [AllowAnonymous]
    [EnableRateLimiting("public")]
    public sealed class StartModel(ReadSetupV1 setup, CreateOwnerV1 createOwner, ReadSetupLocalesV1 readLocales, ReadInstancePresentationV1 readPresentation, IAntiforgery antiforgery, UiText texts, FrontendAssets assets)
        : JularrPage
    {
        private bool _requiresOwner;
        private IReadOnlyList<SetupLocaleV1> _locales = [];
        private CreateOwnerDataV1? _input;

        protected override void Define(Page page)
        {
            page.Title("setup.start.title");
            page.Asset("Framework/Components/PasswordReveal.entry.ts");

            // Render only available setup.
            if (!_requiresOwner)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                page.Content.Add(new P(new Text("setup.owner.closed")));
                page.Content.Add(new A(new Text("account.login.submit")).Attribute("href", "/Account/Login"));
                return;
            }

            // Each language shows its flag at the start of the field where one exists.
            var options = _locales.Select(locale => new SelectOption(locale.Id.ToString(CultureInfo.InvariantCulture),
                locale.IsMachineTranslated ? string.Format(CultureInfo.CurrentCulture, texts["setup.locale.machine"], locale.Name) : locale.Name,
                WorkLanguages.FlagIcon(locale.Locale, assets)));
            var form = new Form<CreateOwnerDataV1>(form =>
            {
                form.Text(item => item.Username, "setup.owner.userName", "username");
                form.Text(item => item.ProfileName, "profile.field.displayName", "nickname");
                form.Password(item => item.Password, "account.field.password", "new-password", new FieldHelp(Trailing: new PasswordReveal(assets)));
                form.Password(item => item.PasswordConfirmation, "setup.owner.passwordConfirmation", "new-password", new FieldHelp(Info: "", Trailing: new PasswordReveal(assets)));
                form.Select(item => item.UiLocaleId, "profile.field.uiLocale", options);
                form.Submit("setup.owner.create");
            }, Url.Page("/Setup/Start/Start"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input, FormError);
            page.Content.Add(new Div(form).Class("ui-section setup-owner"));
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

        public async Task<IActionResult> OnPostAsync(string? username, string? profileName, string? password, string? passwordConfirmation, long uiLocaleId)
        {
            // Invoke the canonical bootstrap service.
            _input = new CreateOwnerDataV1(username ?? "", profileName ?? "", password ?? "", passwordConfirmation ?? "", uiLocaleId);
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
