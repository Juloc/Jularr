using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Instance.V1;
using Jularr.Backend.Data.Public.Localization.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Instance.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Appearance;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Account.Register
{
    // A visitor asks for an account; it stays disabled until an administrator enables it.
    [EnableRateLimiting("public")]
    public sealed class RegisterModel(ReadInstancePresentationV1 presentation, ReadUiLocalesV1 readLocales, RegisterV1 register, IAntiforgery antiforgery, FrontendAssets assets)
        : JularrPage
    {
        private IReadOnlyList<UiLocaleDetailsV1> _locales = [];
        private RegisterDataV1? _input;
        private string? _pendingName;

        protected override void Define(Page page)
        {
            page.Shell = false;
            page.Asset("Pages/Account/Login/Login.entry.scss");
            page.Asset("Modules/Appearance/appearance.entry.scss");
            page.Asset("Framework/Components/PasswordReveal.entry.ts");
            if (_pendingName is not null)
            {
                // A request stays disabled until an administrator enables it.
                page.Title("account.pending.title");
                page.Content.Add(new Section(
                    new Brand(assets, login: true),
                    new Div(new Icon("shield-check", assets, 33)).Class("auth-emblem"),
                    new Span(new Span(), new Text("account.pending.status")).Class("auth-status"),
                    new H1(new Text("account.pending.title")),
                    new P(new NamedText("account.pending.waiting", _pendingName)).Class("auth-waiting"),
                    new Div(new Strong(new Text("account.pending.nextTitle")), new Span(new Text("account.pending.nextText")), new Small(new Text("account.pending.noEmail")))
                        .Class("auth-explain"),
                    new A(new Text("account.register.backToSignIn")).Class("auth-submit").Attribute("href", "/Account/Login")).Class("login-card login-card--pending").Attribute("role", "status"));
                page.Content.Add(new AppearanceDock(assets));
                return;
            }

            page.Title("account.register.title");
            page.Content.Add(new Section(
                new Brand(assets, login: true),
                new H1(new Text("account.register.title")),
                new Form<RegisterDataV1>(form =>
                {
                    form.Text(x => x.Email, "account.field.email", "email", new FieldHelp(Info: ""));
                    form.Password(x => x.Password, "account.field.password", "new-password",
                        new FieldHelp("account.register.passwordPlaceholder", "", Trailing: new PasswordReveal(assets)));
                    form.Password(x => x.PasswordConfirmation, "setup.owner.passwordConfirmation", "new-password",
                        new FieldHelp("account.register.confirmPlaceholder", "", Trailing: new PasswordReveal(assets)));
                    form.Hidden(x => x.UiLocaleId);
                    form.Content(new Div(
                        new Span(new Icon("shield-check", assets, 18)).Class("auth-note__icon"),
                        new Span(new Strong(new Text("account.register.approvalTitle")), new Small(new Text("account.register.approvalText")))).Class("auth-note"));
                    form.Submit(Styled(new Button(new Text("account.register.submit"), new Span(new ValueText("→")).Attribute("aria-hidden", "true")), "auth-submit"));
                }, Url.Page("/Account/Register/Register")!, antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input, FormError),
                new A(new Span(new ValueText("←")).Attribute("aria-hidden", "true"), new Text("account.register.backToSignIn")).Class("auth-back").Attribute("href", "/Account/Login"))
                .Class("login-card login-card--register"));
            page.Content.Add(new AppearanceDock(assets));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!await IsOpenAsync())
            {
                return NotFound();
            }

            // The account that was just requested, shown once.
            _pendingName = TempData["Registered"] is true ? TempData["RegisteredName"] as string ?? "" : null;
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? email, string? password, string? passwordConfirmation, long uiLocaleId)
        {
            if (!await IsOpenAsync())
            {
                return NotFound();
            }

            // A name for the profile comes from the address until the person picks their own; passwords are never written back into the form.
            var name = (email ?? "").Split('@')[0].Trim();
            var displayName = name.Length == 0 ? (email ?? "").Trim() : name;
            _input = new RegisterDataV1(email ?? "", displayName, "", "", uiLocaleId);
            await register.ExecuteAsync<RegisterResultV1>(new AuthenticationParametersV1(),
                new RegisterDataV1(email ?? "", displayName, password ?? "", passwordConfirmation ?? "", uiLocaleId), HttpContext.RequestAborted);
            TempData["Registered"] = true;
            TempData["RegisteredName"] = (email ?? "").Trim();
            return RedirectToPage("/Account/Register/Register");
        }

        // A sentence that names the new account at the place {0} stands.
        private sealed class NamedText(string key, string name) : IUiComponent
        {
            public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
            {
                var parts = texts[key].Split("{0}", 2);
                encoder.Encode(writer, parts[0]);
                new Strong(new ValueText(name)).WriteTo(writer, encoder, texts);
                if (parts.Length > 1) encoder.Encode(writer, parts[1]);
            }
        }

        private static Button Styled(Button button, string cssClass)
        {
            button.Class(cssClass);
            return button;
        }

        // Without the Registration module the page does not exist.
        private async Task<bool> IsOpenAsync() =>
            (await presentation.ExecuteAsync<InstancePresentationDetailsV1>(new ReadInstancePresentationParametersV1(), HttpContext.RequestAborted)).AllowsRegistration;

        protected override async Task LoadDataAsync()
        {
            _locales = await readLocales.ExecuteAsync<BaseDataList<UiLocaleDetailsV1>>(cancellationToken: HttpContext.RequestAborted);
            var instance = (await presentation.ExecuteAsync<InstancePresentationDetailsV1>(new ReadInstancePresentationParametersV1(), HttpContext.RequestAborted)).UiLocaleId;
            _input ??= new RegisterDataV1("", "", "", "", LocalePreference.Preferred(Request, _locales, instance));
        }
    }
}
