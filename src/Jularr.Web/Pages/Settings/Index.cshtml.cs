using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

public sealed class IndexModel(AppDbContext db, CurrentAccountContext account, IInstanceModuleService modules) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlySet<string> SettingsDestinations { get; private set; } = new HashSet<string>();
    public bool CanConfigureBooks { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var settings = await modules.GetAsync(cancellationToken);
        var enabled = settings.Modules.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet();
        SettingsDestinations = UiShellNavigation.BuildSection("settings", account.Can, enabled)!.Groups!.SelectMany(group => group.Items).Select(item => item.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CanConfigureBooks = account.Can(JularrPolicies.AdminSystem) && settings.IsEnabled(InstanceModule.Book);
    }
}
