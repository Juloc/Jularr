using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// The shared Provider UI of Admin: the provider list per family and the detail form of the selected provider, rendered from each provider's
/// <see cref="ProviderView"/>. The save, test and remove handlers work on any <see cref="IProviderSettings"/> and also serve the Setup provider step
/// (it posts here with a local <c>returnUrl</c>), so both surfaces write the same configuration through the same operations.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
[EnableRateLimiting(DiscoveryRegistration.ProviderSettingsRateLimitPolicy)]
[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(ValueLengthLimit = TmdbCredentialStore.MaxReadAccessTokenLength + 16)]
public sealed class ProvidersModel(AppDbContext db, IEnumerable<IProviderSettings> providers) : PageModel
{
    private const int MaxRequestBytes = 16 * 1024;

    [BindProperty(SupportsGet = true)]
    public string? Provider { get; set; }

    public ProviderPageModel View { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var views = new List<ProviderView>();
        foreach (var settings in providers)
        {
            views.Add(await settings.GetViewAsync(cancellationToken));
        }

        var (feedback, feedbackKey) = ProviderPageModel.Take(TempData);
        var selected = views.Any(view => view.Key == Provider) ? Provider : views.FirstOrDefault()?.Key;
        View = new ProviderPageModel(ui, views, selected, Url.Page("/Admin/Providers") ?? "/Admin/Providers", feedback, feedbackKey);
    }

    public async Task<IActionResult> OnPostSaveAsync(bool enabled, string? returnUrl, CancellationToken cancellationToken)
    {
        if (Find() is not { } settings)
        {
            return NotFound();
        }

        ProviderPageModel.Remember(TempData, settings.Key, await settings.SaveAsync(enabled, SecretsOf(await settings.GetViewAsync(cancellationToken)), cancellationToken));
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostTestAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        if (Find() is not { } settings)
        {
            return NotFound();
        }

        ProviderPageModel.Remember(TempData, settings.Key, await settings.TestAsync(SecretsOf(await settings.GetViewAsync(cancellationToken)), cancellationToken));
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        if (Find() is not { } settings)
        {
            return NotFound();
        }

        await settings.RemoveSavedValuesAsync(cancellationToken);
        ProviderPageModel.Remember(TempData, settings.Key, ProviderFeedback.Removed);
        return Back(returnUrl);
    }

    private IProviderSettings? Find() => providers.FirstOrDefault(settings => settings.Key == Provider);

    /// <summary>The values the form carries for the fields of the provider's schema, and nothing else.</summary>
    private Dictionary<string, string?> SecretsOf(ProviderView view) => view.Fields.ToDictionary(field => field.Name, field => (string?)Request.Form[field.Name].ToString());

    /// <summary>Back to the surface that posted: only a local address is followed, anything else lands on this page.</summary>
    private IActionResult Back(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage();
}
