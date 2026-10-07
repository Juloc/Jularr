using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Home;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>Owner-only server-wide module switches. Disabling a module never deletes its data.</summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class InstanceModel(
    AppDbContext db,
    IInstanceModuleService modules) : PageModel
{
    // Expose a switch only after its complete vertical is gated. Future modules already have
    // stable enum identities, but are added here one by one as their runtime slice is completed.
    public static IReadOnlyList<InstanceModule> ConfigurableMediaModules { get; } =
    [
        InstanceModule.Anime,
        InstanceModule.Movie,
        InstanceModule.Tv,
        InstanceModule.Manga,
        InstanceModule.Novel,
        InstanceModule.Book,
        InstanceModule.Audiobook,
        InstanceModule.Music
    ];

    public static IReadOnlyList<InstanceModule> ConfigurableFeatureModules { get; } =
    [
        InstanceModule.Learning,
        InstanceModule.Acquisition,
        InstanceModule.Playback,
        InstanceModule.Tracking
    ];

    public static IEnumerable<InstanceModule> ConfigurableModules =>
        ConfigurableMediaModules.Concat(ConfigurableFeatureModules);

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public InstanceModuleSettings Settings { get; private set; } =
        InstanceModuleSettings.Default;

    /// <summary>The Home/Discover layout every profile starts from; a profile that arranged its own is not touched.</summary>
    public HomeEditorModel HomeEditor { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public InstancePreset Preset => InstanceModulePresets.Detect(Settings);

    public string PresetName(InstancePreset preset) => Ui[$"admin.instance.preset.{preset.ToString().ToLowerInvariant()}"];

    public string PresetDescription(InstancePreset preset) => Ui[$"admin.instance.preset.{preset.ToString().ToLowerInvariant()}.description"];

    /// <summary>A preset only sets the module switches below it; it adds no runtime of its own.</summary>
    public async Task<IActionResult> OnPostPresetAsync(InstancePreset preset, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (preset == InstancePreset.Custom || !Enum.IsDefined(preset))
        {
            return BadRequest();
        }

        await modules.SaveAsync(InstanceModulePresets.Apply(preset, await modules.GetAsync(cancellationToken)), cancellationToken);
        TempData["Status"] = Ui["admin.instance.saved"];
        return RedirectToPage();
    }

    /// <summary>Stores the instance default of the Home/Discover layout. It is a default only: profiles with their own layout keep it, and the media types a module switch removed keep their place for when it returns.</summary>
    public async Task<IActionResult> OnPostHomeAsync(string[]? order, string[]? shown, string? landing, bool prioritizeContinue, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var store = new HomeLayoutStore(db);
        var available = HomeLayoutStore.OrderableFor(await modules.GetAsync(cancellationToken));
        await store.SaveInstanceDefaultAsync(HomeLayoutPolicy.FromEditor(await store.GetInstanceDefaultAsync(cancellationToken), order, shown, landing, prioritizeContinue, available), cancellationToken);
        TempData["Status"] = Ui["admin.instance.home.saved"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var updated = await modules.GetAsync(cancellationToken);
        foreach (var module in ConfigurableModules)
        {
            updated = updated.With(
                module,
                Request.Form.ContainsKey(FieldName(module)));
        }

        await modules.SaveAsync(updated, cancellationToken);

        TempData["Status"] = Ui["admin.instance.saved"];
        return RedirectToPage();
    }

    public static string FieldName(InstanceModule module) =>
        $"module_{module}";

    public string Name(InstanceModule module) => Ui[NameKey(module)];

    /// <summary>The catalog key of a module's name; Setup shows the same module list with the same words.</summary>
    public static string NameKey(InstanceModule module) => $"admin.instance.module.{StorageName(module)}";

    public static string DescriptionKey(InstanceModule module) => $"{NameKey(module)}.description";

    public static string Icon(InstanceModule module) =>
        module switch
        {
            InstanceModule.Anime => "screen-play",
            InstanceModule.Movie => "film",
            InstanceModule.Tv => "tv",
            InstanceModule.Manga or InstanceModule.Book => "book",
            InstanceModule.Novel => "document",
            InstanceModule.Audiobook or InstanceModule.Music => "headphones",
            InstanceModule.Learning => "cap",
            InstanceModule.Acquisition => "download",
            InstanceModule.Playback => "play",
            _ => "chart"
        };

    // Only modules whose configuration has a dedicated Admin page link there; the others have nothing beyond the switch.
    public static string? SettingsPage(InstanceModule module) =>
        module switch
        {
            InstanceModule.Acquisition => "/Admin/Usenet",
            InstanceModule.Playback => "/Admin/Transcoding",
            _ => null
        };

    public string Description(InstanceModule module) => Ui[DescriptionKey(module)];

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Settings = await modules.GetAsync(cancellationToken);
        var available = HomeLayoutStore.OrderableFor(Settings);
        var layout = HomeLayoutPolicy.Resolve(await new HomeLayoutStore(db).GetInstanceDefaultAsync(cancellationToken), available, false);
        HomeEditor = new HomeEditorModel(Ui, "instance-home", HomeEditorModel.ItemsOf(layout.Order, layout.Hidden), layout.Landing, layout.PrioritizeContinue, HomeEditorModel.PresetsFor(available));
    }

    private static string StorageName(InstanceModule module) =>
        module switch
        {
            InstanceModule.Tv => "tv",
            _ => module.ToString().ToLowerInvariant()
        };
}
