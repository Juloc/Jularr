using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Music;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.Music;

/// <summary>The managed artists and the form that adds one: search the metadata provider, choose how its albums are monitored.</summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class IndexModel(AppDbContext db, MusicQuery query, IMusicMetadataProvider provider, MusicLibraryService library, ILogger<IndexModel> logger) : PageModel
{
    public const string PagePath = "/Admin/Music";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<MusicArtistRow> Artists { get; private set; } = [];

    public IReadOnlyList<MusicArtistSummary> Found { get; private set; } = [];

    public string? Search { get; private set; }

    public bool ProviderFailed { get; private set; }

    public string? Notice => TempData["MusicNotice"] as string;

    public string? Error => TempData["MusicError"] as string;

    public async Task OnGetAsync(string? q, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (Search is null)
        {
            return;
        }

        try
        {
            Found = await provider.SearchArtistsAsync(Search, 12, cancellationToken);
        }
        catch (MusicMetadataException exception)
        {
            logger.LogWarning(exception, "The artist search '{Query}' failed.", Search);
            ProviderFailed = true;
        }
    }

    public async Task<IActionResult> OnPostAddAsync(string musicBrainzId, MusicMonitorMode mode, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (string.IsNullOrWhiteSpace(musicBrainzId) || !Enum.IsDefined(mode))
        {
            return BadRequest();
        }

        try
        {
            var artist = await library.AddArtistAsync(musicBrainzId, mode, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
            TempData["MusicNotice"] = Ui.Format("admin.music.added", ("artist", artist.Name));
            return RedirectToPage("Artist", new { id = artist.Id });
        }
        catch (MusicMetadataException exception)
        {
            logger.LogWarning(exception, "Adding artist {MusicBrainzId} failed.", musicBrainzId);
            TempData["MusicError"] = Ui["admin.music.providerFailed"];
            return RedirectToPage();
        }
    }

    public string MonitorLabel(MusicMonitorMode mode) => Ui[$"admin.music.monitor.{mode.ToString().ToLowerInvariant()}"];

    public string RefreshedLabel(DateTime? refreshedAt) => refreshedAt is { } value ? value.ToLocalTime().ToString("g") : Ui["admin.music.neverRefreshed"];

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Artists = await query.ListArtistsAsync(cancellationToken);
    }
}
