using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.Requests;

[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class SettingsModel(AppDbContext db, AcquisitionRequestSettingsStore settings, QualityProfileStore qualityProfiles, IInstanceModuleService instanceModules) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public AcquisitionRequestSettings RequestSettings { get; private set; } = AcquisitionRequestSettings.Default;
    public IReadOnlyList<QualityProfile> QualityProfiles { get; private set; } = [];
    public IReadOnlyList<MediaAcquisitionKind> EnabledKinds { get; private set; } = [];
    [BindProperty] public RequestRuleEditorInput Editor { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long? id, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        var profile = RequestSettings.Rules.Profiles.SingleOrDefault(profile => profile.Id == (id ?? RequestSettings.Rules.DefaultId));
        if (profile is null)
        {
            return NotFound();
        }

        Editor = RequestRuleEditorInput.FromProfile(profile, RequestSettings.Revision);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveRuleAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        try
        {
            if (!ModelState.IsValid)
            {
                throw new ArgumentException("Invalid request rule input.");
            }

            var previous = RequestSettings.Rules.Profiles.SingleOrDefault(profile => profile.Id == Editor.Id);
            var values = Editor.ReadValues(EnabledKinds, previous?.Values.Kinds ?? []);
            var saved = await settings.SaveProfileAsync(Editor.Id, Editor.Name, Editor.Description, values, Editor.Revision, cancellationToken);
            TempData["Status"] = Ui["requestRules.saved"];
            return RedirectToPage(new { id = Editor.Id ?? saved.Rules.NextId - 1 });
        }
        catch (Exception exception) when (exception is ArgumentException or RequestRuleConflictException)
        {
            ModelState.AddModelError(string.Empty, Ui[exception is RequestRuleConflictException ? "requestRules.conflict" : "requestRules.invalid"]);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDuplicateAsync(long id, long revision, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        try
        {
            var profile = RequestSettings.Rules.Profiles.SingleOrDefault(profile => profile.Id == id) ?? throw new ArgumentException("Unknown request rule.");
            var name = Ui.Format("requestRules.copyName", ("name", profile.Name.Length > 45 ? profile.Name[..45] : profile.Name));
            var saved = await settings.DuplicateProfileAsync(id, name, revision, cancellationToken);
            return RedirectToPage(new { id = saved.Rules.NextId - 1 });
        }
        catch (Exception exception) when (exception is ArgumentException or RequestRuleConflictException)
        {
            TempData["Status"] = Ui[exception is RequestRuleConflictException ? "requestRules.conflict" : "requestRules.invalid"];
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostDefaultAsync(long id, long revision, CancellationToken cancellationToken)
    {
        try
        {
            await settings.SetDefaultProfileAsync(id, revision, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or RequestRuleConflictException)
        {
            Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = Ui[exception is RequestRuleConflictException ? "requestRules.conflict" : "requestRules.invalid"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, bool confirmReassignment, long revision, CancellationToken cancellationToken)
    {
        try
        {
            await settings.DeleteProfileAsync(id, confirmReassignment, revision, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or RequestRuleConflictException)
        {
            Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = Ui[exception is RequestRuleConflictException ? "requestRules.conflict" : "requestRules.invalid"];
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostFinishTransitionAsync(long revision, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return BadRequest();
        }

        try
        {
            await settings.FinishApprovalTransitionAsync(revision, cancellationToken);
        }
        catch (RequestRuleConflictException)
        {
            Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = Ui["requestRules.conflict"];
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProfilesAsync(string[]? profiles, CancellationToken cancellationToken)
    {
        var known = (await qualityProfiles.LoadAsync(cancellationToken)).Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        await settings.SetRequesterQualityProfilesAsync((profiles ?? []).Where(known.Contains), cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var instance = await instanceModules.GetAsync(cancellationToken);
        EnabledKinds = Enum.GetValues<MediaAcquisitionKind>().Where(kind => instance.IsEnabled(AcquisitionInstanceModules.For(kind))).ToArray();
        RequestSettings = await settings.LoadAsync(cancellationToken);
        QualityProfiles = (await qualityProfiles.LoadAsync(cancellationToken)).Profiles;
    }
}
