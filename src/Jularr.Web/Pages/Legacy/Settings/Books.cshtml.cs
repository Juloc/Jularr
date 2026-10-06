using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

/// <summary>
/// Settings → Books discovery (#371): the server-wide, owner-only optional Hardcover API key that
/// enriches Trending/Top/New Books rows with a community rating for every profile. Distinct from
/// Books → Integrations, which is a profile's own personal Hardcover reading-list connection.
/// </summary>
public sealed class BooksModel(
    AppDbContext db,
    CurrentAccountContext account,
    IDataProtectionProvider dataProtectionProvider,
    DiscoverySourceFlights discoverySources) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public bool IsOwner => account.IsOwner;
    public bool HardcoverEnabled { get; private set; }

    [BindProperty]
    public string? HardcoverApiKey { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        Ui = await LoadBundleAsync(cancellationToken);
        var store = new BookDiscoverySettingsStore(dataProtectionProvider);

        if (string.IsNullOrWhiteSpace(HardcoverApiKey))
        {
            // An empty field keeps the currently saved key (matches Settings → AI); a saved key is
            // only ever removed through the explicit Remove action below.
            await LoadAsync(cancellationToken);
            return Page();
        }

        await store.SaveAsync(HardcoverApiKey, cancellationToken);
        discoverySources.Clear();
        TempData["Status"] = Ui["settings.books.hardcover.saved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        Ui = await LoadBundleAsync(cancellationToken);
        await new BookDiscoverySettingsStore(dataProtectionProvider).ClearAsync(cancellationToken);
        discoverySources.Clear();
        TempData["Status"] = Ui["settings.books.hardcover.removed"];
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await LoadBundleAsync(cancellationToken);
        HardcoverApiKey = null;

        if (!account.IsOwner)
        {
            HardcoverEnabled = false;
            return;
        }

        var settings = await new BookDiscoverySettingsStore(dataProtectionProvider)
            .LoadAsync(cancellationToken);
        HardcoverEnabled = settings.HardcoverEnabled;
    }

    private Task<UiTextBundle> LoadBundleAsync(CancellationToken cancellationToken) =>
        UiRequestLocalization.GetBundleAsync(HttpContext, db);
}
