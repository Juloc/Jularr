using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

/// <summary>
/// Personal appearance: theme mode and accent colour. Changes are saved through
/// /Appearance/Theme and /Appearance/Accent and previewed live with the server-rendered palette.
/// </summary>
public sealed class AppearanceModel(
    AppDbContext db,
    CurrentAccountContext currentAccount) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public ProfileAppearance Appearance { get; private set; } = ProfileAppearance.Default;
    public InstanceAppearanceSettings InstanceAppearance { get; private set; } = InstanceAppearanceSettings.Default;
    public IReadOnlyList<AccentPreset> Presets => AppAccent.Presets;
    public IReadOnlyList<AppThemeDefinition> Themes => ThemeCatalog.All;
    public string EffectiveAccent => AppAccent.Effective(Appearance.AccentColor);
    public string SelectedThemeId => Appearance.ThemeId is null
        ? string.Empty
        : ThemeCatalog.NormalizeOrOriginal(Appearance.ThemeId);

    /// <summary>Whether the account may reach Admin, so the page offers the sidebar shortcut switch.</summary>
    public bool CanUseAdmin => JularrPolicies.Allows(User, JularrPolicies.AdminMedia);

    public async Task<IActionResult> OnPostAdminShortcutAsync(bool show, CancellationToken cancellationToken)
    {
        if (!CanUseAdmin || string.IsNullOrWhiteSpace(currentAccount.ProfileId))
        {
            return Forbid();
        }

        await new ProfileAppearanceStore(db).SetAdminShortcutAsync(currentAccount.ProfileId, show, cancellationToken);
        return RedirectToPage();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Appearance = await new ProfileAppearanceStore(db).GetAsync(
            currentAccount.ProfileId,
            cancellationToken);
        InstanceAppearance = await new InstanceAppearanceSettingsStore(db)
            .LoadAsync(cancellationToken);
    }
}
