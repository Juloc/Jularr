using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Account;

/// <summary>
/// The provider step of Setup, between creating the owner and finishing: while a provider that an enabled feature needs is unusable, Setup is not
/// complete (or the features that need it are turned off). It renders the shared Provider UI (the provider rows and the detail form) and writes through
/// the same handlers as Admin → Providers; only the completion rule lives here.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class SetupProviderModel(AppDbContext db, IEnumerable<IProviderSettings> providers) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    [BindProperty(SupportsGet = true)]
    public string? Provider { get; set; }

    public ProviderPageModel View { get; private set; } = null!;

    /// <summary>The providers that block finishing, set when Setup was asked to finish while one is unusable.</summary>
    public IReadOnlyList<ProviderView> Blocking { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();
        var (feedback, feedbackKey) = ProviderPageModel.Take(TempData);
        await LoadAsync(feedback, feedbackKey, cancellationToken);
        return View.Providers.Any(provider => provider.RequiredReasonKey is not null) ? Page() : LocalRedirect(ReturnUrl);
    }

    public async Task<IActionResult> OnPostFinishAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();
        await LoadAsync(ProviderFeedback.None, null, cancellationToken);
        Blocking = [.. View.Providers.Where(provider => provider.Blocking is not null)];
        return Blocking.Count == 0 ? LocalRedirect(ReturnUrl) : Page();
    }

    /// <summary>The way out of the blocking result for an instance that does not need the provider: the features that need it are turned off, their data stays.</summary>
    public async Task<IActionResult> OnPostDisableFeaturesAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();
        foreach (var settings in providers.Where(settings => settings.Key == Provider))
        {
            await settings.DisableDependentFeaturesAsync(cancellationToken);
        }

        return LocalRedirect(ReturnUrl);
    }

    private async Task LoadAsync(ProviderFeedback feedback, string? feedbackKey, CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var views = new List<ProviderView>();
        foreach (var settings in providers)
        {
            views.Add(await settings.GetViewAsync(cancellationToken));
        }

        // The form opens for the provider the viewer picked, else for the first one that still needs attention.
        var selected = views.Any(view => view.Key == Provider) ? Provider : views.FirstOrDefault(view => view.Blocking is not null)?.Key;
        View = new ProviderPageModel(ui, views, selected, Url.Page("/Account/SetupProvider", new { returnUrl = ReturnUrl }) ?? "/Account/SetupProvider", feedback, feedbackKey);
    }

    private string SafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
