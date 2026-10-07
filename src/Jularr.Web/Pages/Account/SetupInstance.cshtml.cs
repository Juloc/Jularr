using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Appearance;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Branding;
using Jularr.Web.Features.Home;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Jularr.Web.Pages.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Account;

/// <summary>
/// The instance step of Setup, between creating the owner and the provider step: what this instance does (a starting point and the module switches),
/// what it is called, how it looks to its owner and how Home starts for everyone. It writes the same stores as Admin → Instance, Admin → Appearance and
/// Settings → Appearance (<see cref="IInstanceModuleService"/>, <see cref="InstanceBrandingStore"/>, <see cref="ProfileAppearanceStore"/>) and keeps no
/// setup-only copy, so everything chosen here is changed later where it is changed for any other instance. The Home layout it saves is the instance
/// default of <see cref="HomeLayoutStore"/>: Setup never writes a profile's own layout.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class SetupInstanceModel(
    AppDbContext db,
    IInstanceModuleService modules,
    InstanceBrandingStore branding,
    IEnumerable<IProviderSettings> providers) : PageModel
{
    public const string ThemeField = "themeMode";
    public const string AccentField = "accent";

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    [BindProperty]
    public InstancePreset Start { get; set; } = InstancePreset.MediaManager;

    [BindProperty]
    public string? InstanceName { get; set; }

    [BindProperty]
    public string ThemeMode { get; set; } = AppTheme.System;

    [BindProperty]
    public string? Accent { get; set; }

    /// <summary>The editor's posted fields. Without them (a post that did not come from the editor) the Home default is left as it is.</summary>
    [BindProperty]
    public string[]? Order { get; set; }

    [BindProperty]
    public string[]? Shown { get; set; }

    [BindProperty]
    public string? Landing { get; set; }

    [BindProperty]
    public bool PrioritizeContinue { get; set; } = true;

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    /// <summary>The instance default Home layout in the shared editor; the cards of media types whose module is off are present but hidden until the owner switches the module on.</summary>
    public HomeEditorModel HomeEditor { get; private set; } = null!;

    public InstanceModuleSettings Settings { get; private set; } = InstanceModuleSettings.Default;

    public IReadOnlyList<AccentPreset> AccentPresets => AppAccent.Presets;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();
        await LoadAsync(cancellationToken);
        // A fresh instance has every module on; the starting point offered first is the one most people want, and Full stays one click away.
        Start = InstanceModulePresets.Detect(Settings) == InstancePreset.Custom ? InstancePreset.Custom : InstancePreset.MediaManager;
        if (Start == InstancePreset.MediaManager)
        {
            Settings = InstanceModulePresets.Apply(InstancePreset.MediaManager, Settings);
        }

        var profile = await new ProfileAppearanceStore(db).GetAsync(ProfileId, cancellationToken);
        ThemeMode = profile.ThemeMode;
        Accent = profile.AccentColor;
        InstanceName = (await branding.GetAsync(cancellationToken)).Name;
        LoadHomeEditor(await new HomeLayoutStore(db).GetInstanceDefaultAsync(cancellationToken));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!Enum.IsDefined(Start) || !AppTheme.TryNormalize(ThemeMode, out var themeMode) || !AppAccent.TryNormalize(Accent, out var accent))
        {
            return BadRequest();
        }

        if (!BrandingValidation.TryNormalizeName(InstanceName, out var name))
        {
            ModelState.AddModelError(nameof(InstanceName), Ui["admin.branding.invalid"]);
            Settings = FromForm(await modules.GetAsync(cancellationToken));
            var home = new HomeLayoutStore(db);
            LoadHomeEditor(HomeLayoutPolicy.FromEditor(await home.GetInstanceDefaultAsync(cancellationToken), Order, Shown, Landing, PrioritizeContinue, AllOrderable));
            return Page();
        }

        // The switches are what the owner sees; a starting point other than Custom then sets the feature switches the way it always does.
        var chosen = FromForm(await modules.GetAsync(cancellationToken));
        var saved = Start == InstancePreset.Custom ? chosen : InstanceModulePresets.Apply(Start, chosen);
        await modules.SaveAsync(saved, cancellationToken);

        if (Order is not null)
        {
            var home = new HomeLayoutStore(db);
            var stored = await home.GetInstanceDefaultAsync(cancellationToken);
            await home.SaveInstanceDefaultAsync(HomeLayoutPolicy.FromEditor(stored, Order, Shown, Landing, PrioritizeContinue, HomeLayoutStore.OrderableFor(saved)), cancellationToken);
        }

        var current = await branding.GetAsync(cancellationToken);
        await branding.SaveIdentityAsync(name, current.HueBranding, current.Hue, current.RecolourLogo, cancellationToken);

        var appearance = new ProfileAppearanceStore(db);
        await appearance.SetThemeAsync(ProfileId, themeMode, cancellationToken);
        await appearance.SetAccentAsync(ProfileId, accent, cancellationToken);

        return await ContinueAsync(cancellationToken);
    }

    /// <summary>Leaves everything as it is. The owner can run this step again from Admin → Instance.</summary>
    public async Task<IActionResult> OnPostSkipAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = SafeReturnUrl();
        return await ContinueAsync(cancellationToken);
    }

    public string Name(InstanceModule module) => Ui[InstanceModel.NameKey(module)];

    public string Description(InstanceModule module) => Ui[InstanceModel.DescriptionKey(module)];

    public string PresetName(InstancePreset preset) => Ui[$"admin.instance.preset.{preset.ToString().ToLowerInvariant()}"];

    public string PresetDescription(InstancePreset preset) => Ui[preset == InstancePreset.Custom ? "setup.instance.custom.description" : $"admin.instance.preset.{preset.ToString().ToLowerInvariant()}.description"];

    /// <summary>The module switches as the form shows them (an unticked switch is simply absent from the post).</summary>
    private InstanceModuleSettings FromForm(InstanceModuleSettings current)
    {
        foreach (var module in InstanceModel.ConfigurableModules)
        {
            current = current.With(module, Request.Form.ContainsKey(InstanceModel.FieldName(module)));
        }

        return current;
    }

    /// <summary>The provider step follows while an enabled feature still needs a provider that is not usable; otherwise Setup is complete.</summary>
    private async Task<IActionResult> ContinueAsync(CancellationToken cancellationToken)
    {
        foreach (var settings in providers)
        {
            if ((await settings.GetViewAsync(cancellationToken)).Blocking is not null)
            {
                return RedirectToPage("/Account/SetupProvider", new { returnUrl = ReturnUrl });
            }
        }

        return LocalRedirect(ReturnUrl);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Settings = await modules.GetAsync(cancellationToken);
    }

    private static IReadOnlySet<WorkMediaType> AllOrderable { get; } = HomeLayoutStore.Orderable(WorkMediaTypes.All);

    private void LoadHomeEditor(HomeLayoutPreference preference)
    {
        var layout = HomeLayoutPolicy.Resolve(preference, AllOrderable, false);
        var offered = HomeLayoutStore.OrderableFor(Settings);
        HomeEditor = new HomeEditorModel(Ui, "setup-home", HomeEditorModel.ItemsOf(layout.Order, layout.Hidden, offered), layout.Landing, layout.PrioritizeContinue, HomeEditorModel.PresetsFor(AllOrderable));
    }

    private string ProfileId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private string SafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
