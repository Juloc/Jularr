using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Branding;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>Controls the instance default and which personal appearance overrides are permitted.</summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class AppearanceModel(AppDbContext db, InstanceBrandingStore branding) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<AppThemeDefinition> Themes => ThemeCatalog.All;

    [BindProperty]
    public string DefaultThemeId { get; set; } = ThemeCatalog.Original;

    [BindProperty]
    public bool AllowProfileThemeOverride { get; set; }

    [BindProperty]
    public bool AllowProfileAccentOverride { get; set; }

    public InstanceBrandingSettings Branding { get; private set; } = InstanceBrandingSettings.Default;

    [BindProperty]
    public string? BrandName { get; set; }

    [BindProperty]
    public bool HueBranding { get; set; }

    [BindProperty]
    public int? Hue { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>The instance name and hue branding; the logo has its own form because it is a file.</summary>
    public async Task<IActionResult> OnPostBrandingAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!BrandingValidation.TryNormalizeName(BrandName, out var name) || Hue is < 0 or > 359)
        {
            TempData["Status"] = Ui["admin.branding.invalid"];
            return RedirectToPage();
        }

        await branding.SaveIdentityAsync(name, HueBranding && Hue is not null, Hue, cancellationToken);
        TempData["Status"] = Ui["admin.branding.saved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLogoAsync(IFormFile? logo, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (logo is null || logo.Length > BrandingValidation.MaxLogoBytes)
        {
            TempData["Status"] = Ui[logo is null ? "admin.branding.logo.empty" : "admin.branding.logo.tooLarge"];
            return RedirectToPage();
        }

        await using var upload = logo.OpenReadStream();
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, cancellationToken);
        var problem = await branding.SetLogoAsync(buffer.ToArray(), cancellationToken);
        TempData["Status"] = Ui[problem switch
        {
            BrandingLogoProblem.None => "admin.branding.logo.saved",
            BrandingLogoProblem.Empty => "admin.branding.logo.empty",
            BrandingLogoProblem.TooLarge => "admin.branding.logo.tooLarge",
            BrandingLogoProblem.UnsafeVector => "admin.branding.logo.unsafe",
            _ => "admin.branding.logo.unsupported"
        }];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveLogoAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await branding.RemoveLogoAsync(cancellationToken);
        TempData["Status"] = Ui["admin.branding.logo.removed"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetBrandingAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await branding.ResetAsync(cancellationToken);
        TempData["Status"] = Ui["admin.branding.reset.done"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!ThemeCatalog.TryGet(DefaultThemeId, out var theme))
        {
            ModelState.AddModelError(nameof(DefaultThemeId), Ui["admin.appearance.invalidTheme"]);
            return Page();
        }

        await new InstanceAppearanceSettingsStore(db).SaveAsync(
            new InstanceAppearanceSettings(
                theme.Id,
                AllowProfileThemeOverride,
                AllowProfileAccentOverride),
            cancellationToken);

        TempData["Status"] = Ui["admin.appearance.saved"];
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var settings = await new InstanceAppearanceSettingsStore(db).LoadAsync(cancellationToken);
        DefaultThemeId = settings.DefaultThemeId;
        AllowProfileThemeOverride = settings.AllowProfileThemeOverride;
        AllowProfileAccentOverride = settings.AllowProfileAccentOverride;
        Branding = await branding.GetAsync(cancellationToken);
        BrandName = Branding.Name;
        HueBranding = Branding.HueBranding;
        Hue = Branding.Hue ?? 270;
    }
}
