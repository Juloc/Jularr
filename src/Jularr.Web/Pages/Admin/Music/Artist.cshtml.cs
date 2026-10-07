using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Music;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.Music;

/// <summary>One artist: how its albums are monitored, a discography refresh and every album with where it stands.</summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class ArtistModel(AppDbContext db, MusicQuery query, MusicLibraryService library, ILogger<ArtistModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public MusicArtistView? View { get; private set; }

    public string? Notice => TempData["MusicNotice"] as string;

    public string? Error => TempData["MusicError"] as string;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        View = await query.GetArtistAsync(id, cancellationToken);
        return View is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostMonitorAsync(Guid id, MusicMonitorMode mode, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!Enum.IsDefined(mode))
        {
            return BadRequest();
        }

        try
        {
            await library.SetArtistMonitorAsync(id, mode, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        TempData["MusicNotice"] = Ui["admin.music.saved"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAlbumAsync(Guid id, Guid workId, bool monitored, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await library.SetAlbumMonitoredAsync(workId, monitored, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        TempData["MusicNotice"] = Ui["admin.music.saved"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await library.RefreshArtistAsync(id, cancellationToken);
            TempData["MusicNotice"] = Ui["admin.music.refreshed"];
        }
        catch (MusicMetadataException exception)
        {
            logger.LogWarning(exception, "Refreshing artist {ArtistId} failed.", id);
            TempData["MusicError"] = Ui["admin.music.providerFailed"];
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        return RedirectToPage(new { id });
    }

    public string MonitorLabel(MusicMonitorMode mode) => Ui[$"admin.music.monitor.{mode.ToString().ToLowerInvariant()}"];

    public string StateLabel(MusicAlbumState state) => Ui[$"admin.music.state.{state.ToString().ToLowerInvariant()}"];

    public string TypeLabel(MusicAlbumType type) => Ui[$"admin.music.type.{type.ToString().ToLowerInvariant()}"];
}
