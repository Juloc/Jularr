using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : JularrPage
{
    protected override void Define(Page page)
    {
        page.Shell = false;
        page.Navigation("account.login.title");
    }

    public IActionResult OnGet()
    {
        PreparePage();
        return Page();
    }
}
