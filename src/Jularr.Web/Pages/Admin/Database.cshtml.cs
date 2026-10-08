using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Performance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Jularr.Web.Pages.Admin;

/// <summary>Admin → Database: read-only PostgreSQL evidence (connections, locks, sizes, vacuum state and, where the server provides it, statement statistics).</summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class DatabaseModel(AppDbContext db, DatabaseDiagnosticsService diagnostics, ILogger<DatabaseModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty(SupportsGet = true)]
    public DatabaseStatementRanking Rank { get; set; } = DatabaseStatementRanking.TotalTime;

    public DatabaseDiagnosticsReport? Report { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!Enum.IsDefined(Rank))
        {
            return BadRequest();
        }

        try
        {
            Report = await diagnostics.ReadAsync(Rank, cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(exception, "The database diagnostics could not be read.");
        }

        return Page();
    }
}
