using System.ComponentModel.DataAnnotations;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.Home;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(OwnerAuthService ownerAuth, SecurityEventLog securityEvents) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(AccountForm.MaxUserNameLength)]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public async Task<IActionResult> OnGetAsync(
        [FromServices] AppDbContext db,
        CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();

        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ReturnUrl);
        }

        if (!await ownerAuth.HasOwnerAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Setup", new { returnUrl = ReturnUrl });
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        [FromServices] AppDbContext db,
        CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();

        if (!await ownerAuth.HasOwnerAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Setup", new { returnUrl = ReturnUrl });
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!ModelState.IsValid)
        {
            AccountForm.LocalizeFieldErrors(ModelState, new Dictionary<string, string>
            {
                [nameof(UserName)] = AccountForm.UserNameMessage(Ui),
                [nameof(Password)] = Ui["account.validation.password"]
            });
            return Page();
        }

        var owner = await ownerAuth.ValidateCredentialsAsync(UserName, Password, cancellationToken);
        if (owner is null)
        {
            securityEvents.Record(
                SecurityEventKind.LoginFailed,
                accountId: null,
                UserName,
                HttpContext.Connection.RemoteIpAddress?.ToString());
            ModelState.AddModelError(string.Empty, Ui["account.login.invalid"]);
            return Page();
        }

        var properties = new AuthenticationProperties
        {
            IsPersistent = RememberMe,
            AllowRefresh = true
        };

        if (RememberMe)
        {
            properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30);
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            OwnerAuthService.CreatePrincipal(owner),
            properties);

        securityEvents.Record(
            SecurityEventKind.LoginSucceeded,
            owner.Id,
            owner.UserName,
            HttpContext.Connection.RemoteIpAddress?.ToString());

        // Only a sign-in without a destination follows the landing preference; a link the viewer came from is always honoured.
        return ReturnUrl == "/" && await new HomeLayoutStore(db).GetLandingAsync(owner.Id, cancellationToken) == HomeLanding.Library ? LocalRedirect("/Library") : LocalRedirect(ReturnUrl);
    }

    private string SafeReturnUrl() =>
        Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
