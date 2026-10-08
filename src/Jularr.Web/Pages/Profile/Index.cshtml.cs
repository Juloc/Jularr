using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Profile;

/// <summary>
/// Profile: the signed-in user's account, activity, settings and (for the owner) admin, plus the
/// destinations the phone bottom bar has no room for. <c>/Profile/settings</c>, <c>/Profile/admin</c>
/// and <c>/Profile/unfinished</c> are the drill-in lists of those sections; Unfinished (#870) is
/// how a phone reaches destinations whose feature is still incomplete. All lists come from
/// <see cref="UiNavigationCatalog"/>.
/// </summary>
public sealed class IndexModel(
    AppDbContext db,
    CurrentAccountContext account,
    IAppShellService appShell,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<UiNavigationItem> Links { get; private set; } = [];
    public IReadOnlyList<UiNavigationItem> Elsewhere { get; private set; } = [];

    /// <summary>The drill-in section (Settings or Admin), or null on the Profile list itself.</summary>
    public UiNavigationItem? Section { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? section, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var instanceSettings = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);
        var enabledModules = instanceSettings.Modules
            .Where(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToHashSet();

        var learningVisible = instanceSettings.IsEnabled(InstanceModule.Learning)
            && await new LearningConfigurationStore(db)
                .HasAnyLearningEnabledAsync(account.ProfileId, cancellationToken);
        var media = await appShell.GetMediaAccessAsync(User, cancellationToken);

        if (section is not null)
        {
            // Admin needs admin.media; for everyone else the drill-in does not exist.
            Section = UiShellNavigation.BuildSection(section, account.Can, enabledModules, learningVisible, media.VisibleMediaTypes);
            return Section is null ? NotFound() : Page();
        }

        (Links, Elsewhere) = UiShellNavigation.BuildProfile(
            learningVisible,
            account.Can,
            media.VisibleMediaTypes,
            enabledModules,
            InstanceModulePresets.Detect(instanceSettings) == InstancePreset.MediaManager);

        // The shell account footer (theme, sign out, version) is shown here on phones and
        // reads the same view data the layout sets for the sidebar.
        var appearance = await new ProfileAppearanceStore(db).GetAsync(account.ProfileId, cancellationToken);
        ViewData["UiTextBundle"] = Ui;
        ViewData["AppThemeMode"] = appearance.ThemeMode;
        return Page();
    }
}
