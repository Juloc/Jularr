using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Home;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

/// <summary>
/// The viewer's own Home/Discover layout (#877): the order and visibility of the media types, the landing page and the Continue priority, plus the
/// theme mode and accent through their existing owner. The same page is the first-login onboarding (<c>?welcome=true</c>): skipping it changes nothing,
/// and a layout that was never saved keeps following the instance default.
/// </summary>
[Authorize]
public sealed class HomeModel(AppDbContext db, CurrentAccountContext currentAccount, IAppShellService shell) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    /// <summary>Whether the page is the first-login onboarding rather than the Settings page.</summary>
    public bool Welcome { get; private set; }

    public HomeEditorModel Editor { get; private set; } = null!;

    public ProfileAppearance Appearance { get; private set; } = ProfileAppearance.Default;

    public InstanceAppearanceSettings InstanceAppearance { get; private set; } = InstanceAppearanceSettings.Default;

    /// <summary>Whether the viewer has its own layout, so a reset has something to remove.</summary>
    public bool IsCustomized { get; private set; }

    public IReadOnlyList<AccentPreset> AccentPresets => AppAccent.Presets;

    public string EffectiveAccent => AppAccent.Effective(Appearance.AccentColor);

    public async Task OnGetAsync(bool welcome, CancellationToken cancellationToken)
    {
        Welcome = welcome;
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveAsync(string[]? order, string[]? shown, string? landing, bool prioritizeContinue, string? theme, string? accent, bool welcome, CancellationToken cancellationToken)
    {
        var store = new HomeLayoutStore(db);
        var available = await AvailableAsync(cancellationToken);
        var profile = await store.GetProfileAsync(currentAccount.ProfileId, cancellationToken);
        var stored = profile.Override ?? await store.GetInstanceDefaultAsync(cancellationToken);
        await store.SaveProfileAsync(currentAccount.ProfileId, HomeLayoutPolicy.FromEditor(stored, order, shown, landing, prioritizeContinue, available), cancellationToken);

        // Only what the viewer actually changed is written: the form always posts the current theme and accent, and echoing them back would turn the defaults into explicit choices.
        var appearance = new ProfileAppearanceStore(db);
        var current = await appearance.GetAsync(currentAccount.ProfileId, cancellationToken);
        if (AppTheme.TryNormalize(theme, out var mode) && mode != current.ThemeMode)
        {
            await appearance.SetThemeAsync(currentAccount.ProfileId, mode, cancellationToken);
        }

        var instanceAppearance = await new InstanceAppearanceSettingsStore(db).LoadAsync(cancellationToken);
        if (AppAccent.TryNormalize(accent, out var seed) && seed is not null && seed != AppAccent.Effective(current.AccentColor) && instanceAppearance.AllowProfileAccentOverride)
        {
            await appearance.SetAccentAsync(currentAccount.ProfileId, seed, cancellationToken);
        }

        return welcome ? Redirect("/") : RedirectToPage();
    }

    /// <summary>Leaves the onboarding without choosing: the instance default keeps applying and the offer does not come back.</summary>
    public async Task<IActionResult> OnPostSkipAsync(CancellationToken cancellationToken)
    {
        await new HomeLayoutStore(db).SkipOnboardingAsync(currentAccount.ProfileId, cancellationToken);
        return Redirect("/");
    }

    /// <summary>Removes the viewer's override: the current instance default applies again, now and when the administrator changes it later.</summary>
    public async Task<IActionResult> OnPostResetAsync(CancellationToken cancellationToken)
    {
        await new HomeLayoutStore(db).ResetProfileAsync(currentAccount.ProfileId, cancellationToken);
        return RedirectToPage();
    }

    private async Task<IReadOnlySet<WorkMediaType>> AvailableAsync(CancellationToken cancellationToken) =>
        HomeLayoutStore.Orderable((await shell.GetMediaAccessAsync(User, cancellationToken)).VisibleMediaTypes);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var available = await AvailableAsync(cancellationToken);
        var layout = await new HomeLayoutStore(db).ResolveAsync(currentAccount.ProfileId, available, cancellationToken);
        IsCustomized = layout.IsCustomized;
        Editor = new HomeEditorModel(Ui, "home-layout", HomeEditorModel.ItemsOf(layout.Order, layout.Hidden), layout.Landing, layout.PrioritizeContinue, HomeEditorModel.PresetsFor(available));
        Appearance = await new ProfileAppearanceStore(db).GetAsync(currentAccount.ProfileId, cancellationToken);
        InstanceAppearance = await new InstanceAppearanceSettingsStore(db).LoadAsync(cancellationToken);
    }
}
