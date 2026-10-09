using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class LibraryModel(AppDbContext db, WorkQueryService works, IInstanceModuleService modules) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<Work> Works { get; private set; } = [];
    public string? Search { get; private set; }
    public int PageNumber { get; private set; }
    public int PageCount { get; private set; }

    public async Task OnGetAsync(string? q, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var settings = await modules.GetAsync(cancellationToken);
        var types = WorkMediaTypes.All.Where(type => InstanceModuleMedia.IsCapabilityFamilyEnabled(settings, type)).ToArray();
        Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 200)];
        (Works, PageNumber, PageCount) = await works.ReadManagementPageAsync(types, settings.IsEnabled(InstanceModule.Anime), Search, pageNumber, cancellationToken);
    }

    public static string? ManagementPath(Work work) => work.MediaType switch
    {
        WorkMediaType.Movie => VideoWorkLinks.AdminPath(MediaAcquisitionKind.Movie, work.Id),
        WorkMediaType.Series => VideoWorkLinks.AdminPath(MediaAcquisitionKind.Tv, work.Id),
        WorkMediaType.Book => $"/Admin/Books/Work/{work.Id:D}",
        WorkMediaType.Music => $"/Admin/Music/Album/{work.Id:D}",
        _ => null
    };
}
