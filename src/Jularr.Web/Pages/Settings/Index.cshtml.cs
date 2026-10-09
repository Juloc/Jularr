using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Microsoft.EntityFrameworkCore;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

public sealed class IndexModel(AppDbContext db, IConfiguration configuration) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public bool PlexConnectionVisible { get; private set; };
    public bool PlexLoginLinked { get; private set; }

    public async Task OnGetAsync()
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var accountId = OwnerAuthService.GetAccountId(User);
        if (accountId is null)
        {
            return;
        }

        PlexLoginLinked = await db.AccountLoginIdentities.AsNoTracking().AnyAsync(
            x => x.AccountId == accountId && x.Provider == "plex",
            HttpContext.RequestAborted);
        PlexConnectionVisible = PlexLoginLinked ||
            configuration.GetValue<bool>("Plex:LinkEnabled") &&
            !string.IsNullOrWhiteSpace(configuration["Plex:ClientIdentifier"]);
    }
}
