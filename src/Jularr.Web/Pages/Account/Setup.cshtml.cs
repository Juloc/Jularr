using System.ComponentModel.DataAnnotations;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Account;

[AllowAnonymous]
public sealed class SetupModel(OwnerAuthService ownerAuth) : PageModel
{
    [BindProperty]
    [Required]
    [StringLength(AccountForm.MaxUserNameLength, MinimumLength = 1)]
    public string UserName { get; set; } = "owner";

    [BindProperty]
    [Required]
    [MinLength(AccountForm.MinPasswordLength)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(Password))]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public async Task<IActionResult> OnGetAsync(
        [FromServices] AppDbContext db,
        CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();

        if (await ownerAuth.HasOwnerAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        [FromServices] AppDbContext db,
        CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();

        if (await ownerAuth.HasOwnerAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!ModelState.IsValid)
        {
            AccountForm.LocalizeFieldErrors(ModelState, new Dictionary<string, string>
            {
                [nameof(UserName)] = AccountForm.UserNameMessage(Ui),
                [nameof(Password)] = AccountForm.NewPasswordMessage(Ui),
                [nameof(ConfirmPassword)] = Ui["account.validation.confirmPassword"]
            });
            return Page();
        }

        OwnerAccount owner;
        try
        {
            owner = await ownerAuth.CreateOwnerAsync(UserName, Password, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }
        catch (ArgumentException exception)
        {
            AccountForm.AddCreationError(ModelState, Ui, exception);
            return Page();
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            OwnerAuthService.CreatePrincipal(owner));

        TempData["Status"] = Ui["account.setup.created"];

        // The instance step decides what this instance does; only then is it known which providers it needs (the provider step follows from there).
        return RedirectToPage("/Account/SetupInstance", new { returnUrl = ReturnUrl });
    }

    private string SafeReturnUrl() =>
        Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
