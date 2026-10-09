using System.ComponentModel.DataAnnotations;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class UsersModel(
    AppDbContext db,
    OwnerAuthService authService,
    AdminAccountService adminAccounts,
    ILogger<UsersModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public PageResult<LocalAccountSummary> UserPage { get; private set; } =
        PageResult<LocalAccountSummary>.From([], new PageRequest());

    public IReadOnlyList<LocalAccountSummary> Users => UserPage.Items;

    public long TotalCount => UserPage.TotalCount ?? 0;

    public long PageCount => Math.Max(1L, (TotalCount + PageSize - 1) / PageSize);

    [BindProperty(SupportsGet = true, Name = "page")]
    public int CurrentPage { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "pageSize")]
    public int PageSize { get; set; } = PageRequest.DefaultPageSize;

    [BindProperty]
    [Required]
    [StringLength(80)]
    public string UserName { get; set; } = "";

    [BindProperty]
    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    [MinLength(12, ErrorMessage = "Password must be at least 12 characters long.")]
    public string Password { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await LoadAsync(cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            await authService.CreateUserAsync(UserName, Password, cancellationToken);
            TempData["Status"] = Ui.Format("admin.users.created", ("userName", UserName.Trim()));
            return RedirectToPage();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public Task<IActionResult> OnPostApproveAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(
            accountId,
            enabled: true,
            successMessageKey: "admin.users.approved",
            cancellationToken);

    public Task<IActionResult> OnPostDisableAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(
            accountId,
            enabled: false,
            successMessageKey: "admin.users.disabled",
            cancellationToken);

    private async Task<IActionResult> SetEnabledAsync(
        string accountId,
        bool enabled,
        string successMessageKey,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            await authService.SetEnabledAsync(accountId, enabled, cancellationToken);
            TempData["Status"] = Ui[successMessageKey];
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Updating account {AccountId} (enabled={Enabled}) failed", accountId, enabled);
            TempData["Status"] = Ui["admin.users.updateFailed"];
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        UserPage = await adminAccounts.ReadUsersV1(
            User,
            new PageRequest(CurrentPage, PageSize),
            cancellationToken);
}
