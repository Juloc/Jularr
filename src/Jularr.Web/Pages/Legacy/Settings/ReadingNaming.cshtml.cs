using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Naming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

// The reading counterpart of /Settings/Naming (#529): one naming profile per reading media type
// (Books, Manga, Light Novels), applied when the reading import pipeline places a file into that
// media type's NAS library root (#389). Unlike anime naming there is no per-library-root or
// per-work assignment: each reading type has exactly one NAS library root today.
[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class ReadingNamingModel(
    ReadingNamingProfileStore store,
    AppDbContext db,
    ILogger<ReadingNamingModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty]
    public ProfileInput Input { get; set; } = ProfileInput.From(ReadingNamingPresets.Default(MediaAcquisitionKind.Book));

    [BindProperty(SupportsGet = true)]
    public MediaAcquisitionKind Kind { get; set; } = MediaAcquisitionKind.Book;

    [BindProperty(SupportsGet = true)]
    public string? Preset { get; set; }

    public IReadOnlyList<MediaAcquisitionKind> Kinds { get; private set; } = ReadingNamingPresets.ReadingKinds;
    public string BackUrl { get; private set; } = "/Settings/Acquisition";
    public IReadOnlyList<string> ValidationErrors { get; private set; } = [];
    public IReadOnlyList<ReadingNamingPreviewLine> Preview { get; private set; } = [];
    public string? Notice => TempData["ReadingNamingNotice"] as string;
    public string? Error => TempData["ReadingNamingError"] as string;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadKindsAsync(cancellationToken);
        if (Kinds.Count == 0)
        {
            return NotFound();
        }

        if (!Kinds.Contains(Kind))
        {
            Kind = Kinds[0];
        }

        Input = Preset switch
        {
            "structured" => ProfileInput.From(ReadingNamingPresets.Structured(Kind)),
            "default" => ProfileInput.From(ReadingNamingPresets.Default(Kind)),
            _ => ProfileInput.From(await store.ResolveAsync(Kind, cancellationToken))
        };

        BuildPreview();
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadKindsAsync(cancellationToken);
        Kind = Input.MediaKind;
        if (!Kinds.Contains(Kind))
        {
            return NotFound();
        }
        BuildPreview();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadKindsAsync(cancellationToken);
        Kind = Input.MediaKind;
        if (!Kinds.Contains(Kind))
        {
            return NotFound();
        }
        BuildPreview();
        if (ValidationErrors.Count > 0)
        {
            return Page();
        }

        var profile = Input.ToProfile();
        try
        {
            await store.UpsertAsync(profile, cancellationToken);
            TempData["ReadingNamingNotice"] = Ui.Format("settings.readingNaming.status.saved", ("name", profile.Name));
        }
        catch (InvalidDataException exception)
        {
            logger.LogError(exception, "Saving reading naming profile failed");
            TempData["ReadingNamingError"] = Ui["common.error.unexpected"];
        }

        return RedirectToPage(new { kind = profile.MediaKind });
    }

    private async Task LoadKindsAsync(CancellationToken cancellationToken)
    {
        var instance = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);
        Kinds = ReadingNamingPresets.ReadingKinds
            .Where(kind => instance.IsEnabled(AcquisitionInstanceModules.For(kind)))
            .ToArray();
        BackUrl = instance.IsEnabled(InstanceModule.Anime)
            ? "/Settings/Naming"
            : "/Settings/Acquisition";
    }

    private void BuildPreview()
    {
        var profile = Input.ToProfile();
        ValidationErrors = ReadingNamingFormatter.Validate(profile, ReadingNamingSamples.Request(profile.MediaKind));
        Preview = ValidationErrors.Count == 0 ? ReadingNamingSamples.Preview(profile) : [];
    }

    public sealed class ProfileInput
    {
        public MediaAcquisitionKind MediaKind { get; set; }
        public string Name { get; set; } = "";
        public string SeriesFolderFormat { get; set; } = "";
        public string FileFormat { get; set; } = "";

        public static ProfileInput From(ReadingNamingProfile profile) =>
            new()
            {
                MediaKind = profile.MediaKind,
                Name = profile.Name,
                SeriesFolderFormat = profile.SeriesFolderFormat,
                FileFormat = profile.FileFormat
            };

        public ReadingNamingProfile ToProfile() =>
            new(
                MediaKind,
                (Name ?? "").Trim(),
                SeriesFolderFormat ?? "",
                FileFormat ?? "");
    }
}
