using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

public sealed record AcquisitionProfileRow(QualityProfile Profile, IReadOnlyList<MediaAcquisitionKind> DefaultKinds, int Titles);

/// <summary>
/// The one Admin editor of Acquisition Profiles, for every media type that has an acquisition kind: the profile list with where each is used,
/// and the selected profile's quality order, upgrade policy, release rules and waiting steps. It edits the generic <see cref="QualityProfile"/>
/// of the shared store; a media type only decides which profile is its default and which rule fields its releases carry.
/// </summary>
[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class AcquisitionProfilesModel(AppDbContext db, QualityProfileStore store, MediaAcquisitionRegistry registry, IInstanceModuleService modules) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<AcquisitionProfileRow> Rows { get; private set; } = [];

    public IReadOnlyList<MediaAcquisitionKind> Kinds { get; private set; } = [];

    public AcquisitionProfileRow? Selected { get; private set; }

    public QualityProfileForm Form { get; private set; } = new();

    public IReadOnlyList<ProfileEditError> Errors { get; private set; } = [];

    public string? Notice => TempData["ProfilesNotice"] as string;

    public string? Warning => TempData["ProfilesError"] as string;

    public async Task<IActionResult> OnGetAsync(string? id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(id, cancellationToken);
        if (Selected is null && !string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        if (Selected is not null)
        {
            Form = QualityProfileEditing.ToForm(Selected.Profile);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(QualityProfileForm form, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(form.Id, cancellationToken);
        if (Selected is null)
        {
            return NotFound();
        }

        var result = QualityProfileEditing.Parse(form);
        var errors = result.Errors.ToList();
        if (result.Profile is { } profile)
        {
            var allowed = Selected.DefaultKinds.SelectMany(RuleFieldsOf).Distinct().ToHashSet();
            for (var index = 0; allowed.Count > 0 && index < profile.ScoreRules.Length; index++)
            {
                if (!allowed.Contains(profile.ScoreRules[index].Field))
                {
                    errors.Add(new ProfileEditError(nameof(QualityProfileForm.ScoreRules), index + 1, "ruleField", profile.ScoreRules[index].Field.ToString()));
                }
            }

            if (errors.Count == 0)
            {
                await store.UpsertAsync(profile, cancellationToken);
                TempData["ProfilesNotice"] = Ui["admin.profiles.saved"];
                return RedirectToPage(new { id = profile.Id });
            }
        }

        Errors = errors;
        Form = form;
        return Page();
    }

    public async Task<IActionResult> OnPostNewAsync(MediaAcquisitionKind kind, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!registry.Supports(kind))
        {
            return BadRequest();
        }

        return RedirectToPage(new { id = await CreateFromAsync(registry.DefaultProfileFor(kind), Ui["admin.profiles.newName"], cancellationToken) });
    }

    public async Task<IActionResult> OnPostDuplicateAsync(string id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var state = await store.LoadAsync(cancellationToken);
        if (state.Profiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } source)
        {
            return NotFound();
        }

        return RedirectToPage(new { id = await CreateFromAsync(source, Ui.Format("admin.profiles.copyName", ("name", source.Name)), cancellationToken) });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(id, cancellationToken);
        if (Selected is null)
        {
            return NotFound();
        }

        if (await store.DeleteAsync(id, cancellationToken))
        {
            TempData["ProfilesNotice"] = Ui["admin.profiles.deleted"];
            return RedirectToPage();
        }

        TempData["ProfilesError"] = Ui.Format("admin.profiles.deleteBlocked", ("usage", UsageText(Selected)));
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSetDefaultAsync(string id, MediaAcquisitionKind kind, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!registry.Supports(kind))
        {
            return BadRequest();
        }

        await store.SetKindDefaultAsync(kind, id, cancellationToken);
        TempData["ProfilesNotice"] = Ui["admin.profiles.saved"];
        return RedirectToPage(new { id });
    }

    public string KindLabel(MediaAcquisitionKind kind) => Ui[MediaKindLabelKeys.Name(kind)];

    public string UsageText(AcquisitionProfileRow row)
    {
        var parts = new List<string>();
        if (row.DefaultKinds.Count > 0)
        {
            parts.Add(Ui.Format("admin.profiles.defaultFor", ("kinds", string.Join(", ", row.DefaultKinds.Select(KindLabel)))));
        }

        if (row.Titles > 0)
        {
            parts.Add(Ui.Format("admin.profiles.usedBy", ("titles", row.Titles)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Where a quality order starts for a media type: the qualities its importer and parser can tell apart.</summary>
    public string KnownQualities(AcquisitionProfileRow row) =>
        string.Join(", ", (row.DefaultKinds.Count == 0 ? Kinds : row.DefaultKinds).SelectMany(kind => registry.DefaultProfileFor(kind).QualityOrder).Distinct(StringComparer.OrdinalIgnoreCase));

    public string RuleFieldNames(AcquisitionProfileRow row) =>
        string.Join(", ", (row.DefaultKinds.Count == 0 ? Kinds : row.DefaultKinds).SelectMany(RuleFieldsOf).Distinct().Select(field => field.ToString()));

    public string ErrorText(ProfileEditError error) =>
        error.Code switch
        {
            "number" => Ui.Format("admin.profiles.error.number", ("field", Ui[$"admin.profiles.field.{FieldKey(error.Field)}"])),
            "tier" => Ui.Format("admin.profiles.error.tier", ("line", error.Line ?? 0)),
            "rule" => Ui.Format("admin.profiles.error.rule", ("line", error.Line ?? 0)),
            "ruleField" => Ui.Format("admin.profiles.error.ruleField", ("line", error.Line ?? 0), ("field", error.Detail ?? "")),
            "quality" => Ui.Format("admin.profiles.error.quality", ("quality", error.Detail ?? "")),
            _ => Ui.Format("admin.profiles.error.profile", ("detail", error.Detail ?? ""))
        };

    private static string FieldKey(string field) => char.ToLowerInvariant(field[0]) + field[1..];

    /// <summary>The release facts a media type's parser actually produces, so a rule can only be written about something that is there.</summary>
    private static IEnumerable<ReleaseRuleField> RuleFieldsOf(MediaAcquisitionKind kind) =>
        kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv or MediaAcquisitionKind.Anime
            ? Enum.GetValues<ReleaseRuleField>()
            : [ReleaseRuleField.RawTitle, ReleaseRuleField.ReleaseGroup];

    private async Task LoadAsync(string? id, CancellationToken cancellationToken)
    {
        var settings = await modules.GetAsync(cancellationToken);
        Kinds = [.. registry.Kinds.Where(kind => settings.IsEnabled(AcquisitionInstanceModules.For(kind))).OrderBy(kind => kind.ToString(), StringComparer.Ordinal)];
        var state = await store.LoadAsync(cancellationToken);
        Rows =
        [
            .. state.Profiles
                .Select(profile => new AcquisitionProfileRow(
                    profile,
                    [.. registry.Kinds.Where(kind => state.DefaultProfileIdFor(kind)?.Equals(profile.Id, StringComparison.OrdinalIgnoreCase) == true)],
                    state.WorkAssignments.Values.Count(assigned => assigned.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(row => row.Profile.Name, StringComparer.OrdinalIgnoreCase)
        ];
        Selected = Rows.FirstOrDefault(row => row.Profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? (string.IsNullOrWhiteSpace(id) ? Rows.FirstOrDefault() : null);
    }

    private async Task<string> CreateFromAsync(QualityProfile source, string name, CancellationToken cancellationToken)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-')]).Trim('-');
        var id = $"{(slug.Length == 0 ? "profile" : slug[..Math.Min(32, slug.Length)])}-{Guid.NewGuid().ToString("N")[..8]}";
        await store.UpsertAsync(source with { Id = id, Name = name }, cancellationToken);
        return id;
    }
}
