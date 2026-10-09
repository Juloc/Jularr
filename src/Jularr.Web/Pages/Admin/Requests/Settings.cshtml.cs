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
public sealed class SettingsModel(AppDbContext db, AcquisitionRequestSettingsStore settings, QualityProfileStore qualityProfiles, OwnerAuthService accounts, IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public AcquisitionRequestSettings RequestSettings { get; private set; } = AcquisitionRequestSettings.Default;
    public IReadOnlyList<QualityProfile> QualityProfiles { get; private set; } = [];
    public IReadOnlyList<MediaAcquisitionKind> EnabledKinds { get; private set; } = [];
    public IReadOnlyDictionary<string, string> ProfileNames { get; private set; } = new Dictionary<string, string>();
    public bool CanEditSettings => JularrPolicies.Allows(User, JularrPolicies.AcquisitionSettings);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadEnabledKindsAsync(cancellationToken);
        RequestSettings = await settings.LoadAsync(cancellationToken);
        QualityProfiles = (await qualityProfiles.LoadAsync(cancellationToken)).Profiles;
        ProfileNames = (await accounts.ListAsync(cancellationToken)).ToDictionary(account => account.Id, account => account.UserName);
    }

    public async Task<IActionResult> OnPostAddRuleAsync(
        string? name,
        string[]? kinds,
        string? profileId,
        int? maxRequests,
        int? periodDays,
        CancellationToken cancellationToken)
    {
        await LoadEnabledKindsAsync(cancellationToken);
        if (!CanEditSettings)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            var quota = maxRequests is null && periodDays is null
                ? null
                : new AutoApprovalQuota(maxRequests ?? 0, periodDays ?? 0);
            var parsedKinds = (kinds ?? [])
                .Select(AcquisitionAccessNames.ParseKind)
                .ToArray();
            if (parsedKinds.Any(kind => !EnabledKinds.Contains(kind)))
            {
                throw new ArgumentException("A disabled media module cannot be added to an auto-approval rule.");
            }

            var rule = AutoApprovalRule.Create(
                name,
                parsedKinds,
                string.IsNullOrWhiteSpace(profileId) ? [] : [profileId],
                quota);
            await settings.AddRuleAsync(rule, cancellationToken);
            TempData["Status"] = ui["admin.requests.autoSaved"];
        }
        catch (ArgumentException)
        {
            TempData["Status"] = ui["admin.requests.autoInvalid"];
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        if (!CanEditSettings)
        {
            return Forbid();
        }

        await settings.SetRuleEnabledAsync(id, enabled, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveRuleAsync(string id, CancellationToken cancellationToken)
    {
        if (!CanEditSettings)
        {
            return Forbid();
        }

        await settings.RemoveRuleAsync(id, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProfilesAsync(string[]? profiles, CancellationToken cancellationToken)
    {
        if (!CanEditSettings)
        {
            return Forbid();
        }

        var known = (await qualityProfiles.LoadAsync(cancellationToken)).Profiles
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        await settings.SetRequesterQualityProfilesAsync((profiles ?? []).Where(known.Contains), cancellationToken);

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui["admin.requests.profilesSaved"];
        return RedirectToPage();
    }


    private async Task LoadEnabledKindsAsync(CancellationToken cancellationToken)
    {
        if (instanceModules is null)
        {
            EnabledKinds = Enum.GetValues<MediaAcquisitionKind>();
            return;
        }

        var instance = await instanceModules.GetAsync(cancellationToken);
        EnabledKinds = Enum.GetValues<MediaAcquisitionKind>()
            .Where(kind => instance.IsEnabled(AcquisitionInstanceModules.For(kind)))
            .ToArray();
    }


}

