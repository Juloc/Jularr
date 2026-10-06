using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

public sealed class LanguageModel(
    AppDbContext db,
    CurrentAccountContext currentAccount) : PageModel
{
    public IReadOnlyList<UiLocaleSummary> Locales { get; private set; } = [];
    public UiLocaleMetadata CurrentLocale { get; private set; } =
        UiTranslationCatalog.ParseLocale(UiTranslationCatalog.SourceLocale);
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty]
    public string Locale { get; set; } = UiTranslationCatalog.SourceLocale;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var store = new UiTranslationCatalogStore(db);
        await store.SyncSourceMessagesAsync(cancellationToken);

        try
        {
            await store.SetProfileLocaleAsync(
                currentAccount.ProfileId,
                Locale,
                cancellationToken);
            TempData["Status"] = "Interface language updated.";
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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var store = new UiTranslationCatalogStore(db);
        await store.SyncSourceMessagesAsync(cancellationToken);

        Locales = await store.ListLocalesAsync(cancellationToken);
        CurrentLocale = await store.GetProfileLocaleAsync(
            currentAccount.ProfileId,
            cancellationToken);
        Locale = CurrentLocale.Locale;
        Ui = await store.LoadProfileBundleAsync(
            currentAccount.ProfileId,
            cancellationToken);
    }
}
