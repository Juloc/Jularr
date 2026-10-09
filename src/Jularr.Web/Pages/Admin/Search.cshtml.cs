using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

public sealed record AdminSearchItem(string Label, string Href, string Icon, bool IsUser = false);
public sealed record AdminSearchGroup(string LabelKey, IReadOnlyList<AdminSearchItem> Items);

[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class SearchModel(AppDbContext db, OwnerAuthService accounts, AcquisitionAccessStore requests, IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public string Query { get; private set; } = string.Empty;
    public IReadOnlyList<AdminSearchGroup> Groups { get; private set; } = [];

    public async Task OnGetAsync(string? q, CancellationToken cancellationToken) => await LoadResultsAsync(q, cancellationToken);

    public async Task<IActionResult> OnGetPreviewAsync(string? q, CancellationToken cancellationToken)
    {
        await LoadResultsAsync(q, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Partial("_AdminSearchResults", this);
    }

    private async Task LoadResultsAsync(string? q, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Query = (q ?? string.Empty).Trim();
        if (Query.Length > 120)
        {
            Query = Query[..120];
        }

        if (Query.Length == 0)
        {
            return;
        }

        var settings = instanceModules is null ? InstanceModuleSettings.Default : await instanceModules.GetAsync(cancellationToken);
        var modules = settings.Modules.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet();
        var destinations = UiShellNavigation.BuildAdminSearchDestinations(policy => JularrPolicies.Allows(User, policy), modules);
        var groups = new List<AdminSearchGroup>();
        if (JularrPolicies.Allows(User, JularrPolicies.AdminSystem))
        {
            var users = await accounts.SearchAsync(Query, cancellationToken);
            groups.Add(new("admin.search.users", users.Select(account => new AdminSearchItem(account.UserName, $"/Admin/User?id={Uri.EscapeDataString(account.Id)}", "users", true)).ToArray()));
        }

        foreach (var destinationGroup in destinations)
        {
            var items = destinationGroup.Items.Where(item => Ui[item.LabelKey].Contains(Query, StringComparison.OrdinalIgnoreCase)).DistinctBy(item => item.Href).Take(8)
                .Select(item => new AdminSearchItem(Ui[item.LabelKey], item.Href, item.Icon)).ToArray();
            groups.Add(new(destinationGroup.TitleKey, items));
        }

        if (modules.Contains(InstanceModule.Acquisition))
        {
            var kinds = Enum.GetValues<MediaAcquisitionKind>().Where(kind => settings.IsEnabled(AcquisitionInstanceModules.For(kind))).ToArray();
            var titles = await requests.SearchTitlesAsync(Query, kinds, cancellationToken);
            groups.Add(new("admin.search.requests", titles.Select(title => new AdminSearchItem(title, RequestsModel.Href(new AdminRequestFilter(Search: title)), "requests")).ToArray()));
        }

        Groups = groups.Where(group => group.Items.Count > 0).ToArray();
    }
}
