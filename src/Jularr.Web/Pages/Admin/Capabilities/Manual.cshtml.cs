using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Admin.Capabilities;

[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class ManualModel(AppDbContext db, AcquisitionAccessStore store, IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<AcquisitionAccessPolicy> Policies { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadEnabledKindsAsync(cancellationToken);
        Policies = (await store.GetPoliciesAsync(cancellationToken)).Where(policy => EnabledKinds.Contains(policy.Kind)).ToArray();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        await LoadEnabledKindsAsync(cancellationToken);
        var policies = new List<AcquisitionAccessPolicy>();
        try
        {
            foreach (var kind in EnabledKinds)
            {
                var value = Request.Form[$"manual.{AcquisitionAccessNames.Kind(kind)}"].ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    policies.Add(new AcquisitionAccessPolicy(kind, AcquisitionAccessNames.ParseManual(value)));
                }
            }
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var policy in policies)
        {
            await store.SavePolicyAsync(policy, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = Ui["admin.requests.policiesSaved"];
        return RedirectToPage();
    }

    private IReadOnlyList<MediaAcquisitionKind> EnabledKinds { get; set; } = Enum.GetValues<MediaAcquisitionKind>();

    private async Task LoadEnabledKindsAsync(CancellationToken cancellationToken)
    {
        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            EnabledKinds = Enum.GetValues<MediaAcquisitionKind>().Where(kind => instance.IsEnabled(AcquisitionInstanceModules.For(kind))).ToArray();
        }
    }
}
