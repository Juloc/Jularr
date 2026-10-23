using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Appearance;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Frontend.Pages.Account.ResetPassword
{
    // The person who holds an administrator's link sets a new password here; the link works once.
    [EnableRateLimiting("public")]
    public sealed class ResetPasswordModel(ResetPasswordV1 reset, IAntiforgery antiforgery, FrontendAssets assets) : JularrPage
    {
        private string _token = "";
        private bool _done;

        protected override void Define(Page page)
        {
            page.Shell = false;
            page.Title("account.reset.title");
            page.Asset("Pages/Account/Login/Login.entry.scss");
            page.Asset("Modules/Appearance/appearance.entry.scss");
            page.Asset("Framework/Components/PasswordReveal.entry.ts");
            if (_done)
            {
                page.Content.Add(new Section(
                    new Brand(assets, login: true),
                    new Div(new Icon("shield-check", assets, 33)).Class("auth-emblem"),
                    new H1(new Text("account.reset.done")),
                    new A(new Text("account.register.backToSignIn")).Class("auth-submit").Attribute("href", "/Account/Login")).Class("login-card login-card--pending").Attribute("role", "status"));
                page.Content.Add(new AppearanceDock(assets));
                return;
            }

            page.Content.Add(new Section(
                new Brand(assets, login: true),
                new H1(new Text("account.reset.title")),
                new Form<ResetPasswordDataV1>(form =>
                {
                    form.Hidden(x => x.Token);
                    form.Password(x => x.NewPassword, "account.password.new", "new-password",
                        new FieldHelp("account.register.passwordPlaceholder", "", Trailing: new PasswordReveal(assets)));
                    form.Password(x => x.NewPasswordConfirmation, "account.password.confirm", "new-password",
                        new FieldHelp(Trailing: new PasswordReveal(assets)));
                    form.Submit(Styled(new Button(new Text("account.reset.submit"), new Icon("arrow-right", assets, 16)), "auth-submit"));
                }, Url.Page("/Account/ResetPassword/ResetPassword")!, antiforgery.GetAndStoreTokens(HttpContext).RequestToken, new ResetPasswordDataV1(_token, "", ""), FormError),
                new A(new Icon("arrow-left", assets, 16), new Text("account.register.backToSignIn")).Class("auth-back").Attribute("href", "/Account/Login"))
                .Class("login-card login-card--register"));
            page.Content.Add(new AppearanceDock(assets));
        }

        public async Task<IActionResult> OnGetAsync(string? token)
        {
            _token = token ?? "";
            _done = TempData["PasswordReset"] is true;
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? token, string? newPassword, string? newPasswordConfirmation)
        {
            // Passwords are never written back into the form.
            _token = token ?? "";
            await reset.ExecuteAsync<ResetPasswordResultV1>(new AuthenticationParametersV1(),
                new ResetPasswordDataV1(_token, newPassword ?? "", newPasswordConfirmation ?? ""), HttpContext.RequestAborted);
            TempData["PasswordReset"] = true;
            return RedirectToPage("/Account/ResetPassword/ResetPassword");
        }

        private static Button Styled(Button button, string cssClass)
        {
            button.Class(cssClass);
            return button;
        }

        protected override Task LoadDataAsync() => Task.CompletedTask;
    }
}
