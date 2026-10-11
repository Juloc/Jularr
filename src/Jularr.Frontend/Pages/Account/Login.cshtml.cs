using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : JularrPage
{
    protected override void Define(Page page)
    {
        page.Shell = false;
        page.Title("account.login.title");
        page.Asset("Pages/Account/Login.entry.scss");
        page.Content.Add(new Section(
            new Brand(login: true),
            new H1(new Text("account.login.title")).Class("visually-hidden"),
            new Form<LoginDataV1>(form =>
            {
                form.Text(x => x.Email, "account.field.email", "username");
                form.Password(x => x.Password, "account.field.password", "current-password");
                form.Checkbox("account.login.remember");
                form.Submit("account.login.submit");
            }),
            new P(new Text("account.login.unavailable")).Class("auth-unavailable")
                .Attribute("role", "status")
        ).Class("auth-card"));
    }

    public IActionResult OnGet()
    {
        PreparePage();
        return Page();
    }
}
